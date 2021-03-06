﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable enable

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CodeRefactorings
{
    [Export(typeof(ICodeRefactoringService)), Shared]
    internal class CodeRefactoringService : ICodeRefactoringService
    {
        private readonly Lazy<ImmutableDictionary<string, Lazy<ImmutableArray<CodeRefactoringProvider>>>> _lazyLanguageToProvidersMap;
        private readonly ConditionalWeakTable<IReadOnlyList<AnalyzerReference>, StrongBox<ImmutableArray<CodeRefactoringProvider>>> _projectRefactoringsMap
             = new ConditionalWeakTable<IReadOnlyList<AnalyzerReference>, StrongBox<ImmutableArray<CodeRefactoringProvider>>>();

        private readonly ConditionalWeakTable<AnalyzerReference, ProjectCodeRefactoringProvider> _analyzerReferenceToRefactoringsMap
            = new ConditionalWeakTable<AnalyzerReference, ProjectCodeRefactoringProvider>();
        private readonly ConditionalWeakTable<AnalyzerReference, ProjectCodeRefactoringProvider>.CreateValueCallback _createProjectCodeRefactoringsProvider
            = new ConditionalWeakTable<AnalyzerReference, ProjectCodeRefactoringProvider>.CreateValueCallback(r => new ProjectCodeRefactoringProvider(r));

        [ImportingConstructor]
        [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
        public CodeRefactoringService(
            [ImportMany] IEnumerable<Lazy<CodeRefactoringProvider, CodeChangeProviderMetadata>> providers)
        {
            // convert set of all code refactoring providers into a map from language to a lazy initialized list of ordered providers.
            _lazyLanguageToProvidersMap = new Lazy<ImmutableDictionary<string, Lazy<ImmutableArray<CodeRefactoringProvider>>>>(
                () =>
                    ImmutableDictionary.CreateRange(
                        DistributeLanguages(providers)
                            .GroupBy(lz => lz.Metadata.Language)
                            .Select(grp => new KeyValuePair<string, Lazy<ImmutableArray<CodeRefactoringProvider>>>(
                                grp.Key,
                                new Lazy<ImmutableArray<CodeRefactoringProvider>>(() => ExtensionOrderer.Order(grp).Select(lz => lz.Value).ToImmutableArray())))));
        }

        private IEnumerable<Lazy<CodeRefactoringProvider, OrderableLanguageMetadata>> DistributeLanguages(IEnumerable<Lazy<CodeRefactoringProvider, CodeChangeProviderMetadata>> providers)
        {
            foreach (var provider in providers)
            {
                foreach (var language in provider.Metadata.Languages)
                {
                    var orderable = new OrderableLanguageMetadata(
                        provider.Metadata.Name, language, provider.Metadata.AfterTyped, provider.Metadata.BeforeTyped);
                    yield return new Lazy<CodeRefactoringProvider, OrderableLanguageMetadata>(() => provider.Value, orderable);
                }
            }
        }

        private ImmutableDictionary<string, Lazy<ImmutableArray<CodeRefactoringProvider>>> LanguageToProvidersMap
            => _lazyLanguageToProvidersMap.Value;

        private ConcatImmutableArray<CodeRefactoringProvider> GetProviders(Document document)
        {
            var allRefactorings = ImmutableArray<CodeRefactoringProvider>.Empty;
            if (LanguageToProvidersMap.TryGetValue(document.Project.Language, out var lazyProviders))
            {
                allRefactorings = lazyProviders.Value;
            }

            return allRefactorings.ConcatFast(GetProjectRefactorings(document.Project));
        }

        public async Task<bool> HasRefactoringsAsync(
            Document document,
            TextSpan state,
            CancellationToken cancellationToken)
        {
            var extensionManager = document.Project.Solution.Workspace.Services.GetRequiredService<IExtensionManager>();

            foreach (var provider in GetProviders(document))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var refactoring = await GetRefactoringFromProviderAsync(
                    document, state, provider, extensionManager, isBlocking: false, cancellationToken).ConfigureAwait(false);

                if (refactoring != null)
                {
                    return true;
                }
            }

            return false;
        }

        public Task<ImmutableArray<CodeRefactoring>> GetRefactoringsAsync(
            Document document,
            TextSpan state,
            CancellationToken cancellationToken)
            => GetRefactoringsAsync(document, state, isBlocking: false, cancellationToken);

        public Task<ImmutableArray<CodeRefactoring>> GetRefactoringsAsync(
            Document document,
            TextSpan state,
            bool isBlocking,
            CancellationToken cancellationToken)
            => GetRefactoringsAsync(document, state, isBlocking, addOperationScope: _ => null, cancellationToken);

        public async Task<ImmutableArray<CodeRefactoring>> GetRefactoringsAsync(
            Document document,
            TextSpan state,
            bool isBlocking,
            Func<string, IDisposable?> addOperationScope,
            CancellationToken cancellationToken)
        {
            using (Logger.LogBlock(FunctionId.Refactoring_CodeRefactoringService_GetRefactoringsAsync, cancellationToken))
            {
                var extensionManager = document.Project.Solution.Workspace.Services.GetRequiredService<IExtensionManager>();
                var tasks = new List<Task<CodeRefactoring?>>();

                foreach (var provider in GetProviders(document))
                {
                    tasks.Add(Task.Run(
                        () =>
                        {
                            var providerName = provider.GetType().Name;
                            using (addOperationScope(providerName))
                            using (RoslynEventSource.LogInformationalBlock(FunctionId.Refactoring_CodeRefactoringService_GetRefactoringsAsync, providerName, cancellationToken))
                            {
                                return GetRefactoringFromProviderAsync(document, state, provider, extensionManager, isBlocking, cancellationToken);
                            }
                        },
                        cancellationToken));
                }

                var results = await Task.WhenAll(tasks).ConfigureAwait(false);
                return results.WhereNotNull().ToImmutableArray();
            }
        }

        private async Task<CodeRefactoring?> GetRefactoringFromProviderAsync(
            Document document,
            TextSpan state,
            CodeRefactoringProvider provider,
            IExtensionManager extensionManager,
            bool isBlocking,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (extensionManager.IsDisabled(provider))
            {
                return null;
            }

            try
            {
                var actions = ArrayBuilder<(CodeAction action, TextSpan? applicableToSpan)>.GetInstance();
                var context = new CodeRefactoringContext(document, state,

                    // TODO: Can we share code between similar lambdas that we pass to this API in BatchFixAllProvider.cs, CodeFixService.cs and CodeRefactoringService.cs?
                    (action, applicableToSpan) =>
                    {
                        // Serialize access for thread safety - we don't know what thread the refactoring provider will call this delegate from.
                        lock (actions)
                        {
                            actions.Add((action, applicableToSpan));
                        }
                    },
                    isBlocking,
                    cancellationToken);

                var task = provider.ComputeRefactoringsAsync(context) ?? Task.CompletedTask;
                await task.ConfigureAwait(false);

                var result = actions.Count > 0
                    ? new CodeRefactoring(provider, actions.ToImmutable())
                    : null;

                actions.Free();

                return result;
            }
            catch (OperationCanceledException)
            {
                // We don't want to catch operation canceled exceptions in the catch block 
                // below. So catch is here and rethrow it.
                throw;
            }
            catch (Exception e)
            {
                extensionManager.HandleException(provider, e);
            }

            return null;
        }

        private ImmutableArray<CodeRefactoringProvider> GetProjectRefactorings(Project project)
        {
            // TODO (https://github.com/dotnet/roslyn/issues/4932): Don't restrict refactorings in Interactive
            if (project.Solution.Workspace.Kind == WorkspaceKind.Interactive)
            {
                return ImmutableArray<CodeRefactoringProvider>.Empty;
            }

            if (_projectRefactoringsMap.TryGetValue(project.AnalyzerReferences, out var refactorings))
            {
                return refactorings.Value;
            }

            return GetProjectRefactoringsSlow(project);

            // Local functions
            ImmutableArray<CodeRefactoringProvider> GetProjectRefactoringsSlow(Project project)
            {
                return _projectRefactoringsMap.GetValue(project.AnalyzerReferences, pId => new StrongBox<ImmutableArray<CodeRefactoringProvider>>(ComputeProjectRefactorings(project))).Value;
            }

            ImmutableArray<CodeRefactoringProvider> ComputeProjectRefactorings(Project project)
            {
                var builder = ArrayBuilder<CodeRefactoringProvider>.GetInstance();
                foreach (var reference in project.AnalyzerReferences)
                {
                    var projectCodeRefactoringProvider = _analyzerReferenceToRefactoringsMap.GetValue(reference, _createProjectCodeRefactoringsProvider);
                    foreach (var refactoring in projectCodeRefactoringProvider.GetRefactorings(project.Language))
                    {
                        builder.Add(refactoring);
                    }
                }

                return builder.ToImmutableAndFree();
            }
        }

        private class ProjectCodeRefactoringProvider
        {
            private readonly AnalyzerReference _reference;
            private ImmutableDictionary<string, ImmutableArray<CodeRefactoringProvider>> _refactoringsPerLanguage;

            public ProjectCodeRefactoringProvider(AnalyzerReference reference)
            {
                _reference = reference;
                _refactoringsPerLanguage = ImmutableDictionary<string, ImmutableArray<CodeRefactoringProvider>>.Empty;
            }

            public ImmutableArray<CodeRefactoringProvider> GetRefactorings(string language)
            {
                return ImmutableInterlocked.GetOrAdd(ref _refactoringsPerLanguage, language, (language, provider) => provider.CreateRefactorings(language), this);
            }

            private ImmutableArray<CodeRefactoringProvider> CreateRefactorings(string language)
            {
                // check whether the analyzer reference knows how to return fixers directly.
                if (_reference is ICodeRefactoringProviderFactory codeRefactoringProviderFactory)
                {
                    return codeRefactoringProviderFactory.GetRefactorings();
                }

                // otherwise, see whether we can pick it up from reference itself
                if (!(_reference is AnalyzerFileReference analyzerFileReference))
                {
                    return ImmutableArray<CodeRefactoringProvider>.Empty;
                }

                var builder = ArrayBuilder<CodeRefactoringProvider>.GetInstance();

                try
                {
                    var analyzerAssembly = analyzerFileReference.GetAssembly();
                    var typeInfos = analyzerAssembly.DefinedTypes;

                    foreach (var typeInfo in typeInfos)
                    {
                        if (typeInfo.IsSubclassOf(typeof(CodeRefactoringProvider)))
                        {
                            try
                            {
                                var attribute = typeInfo.GetCustomAttribute<ExportCodeRefactoringProviderAttribute>();
                                if (attribute != null)
                                {
                                    if (attribute.Languages == null ||
                                        attribute.Languages.Length == 0 ||
                                        attribute.Languages.Contains(language))
                                    {
                                        builder.Add((CodeRefactoringProvider)Activator.CreateInstance(typeInfo.AsType()));
                                    }
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
                catch
                {
                    // REVIEW: is the below message right?
                    // NOTE: We could report "unable to load analyzer" exception here but it should have been already reported by DiagnosticService.
                }

                return builder.ToImmutableAndFree();
            }
        }
    }
}
