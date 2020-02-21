﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.CodeAnalysis.ExternalAccess.UnitTesting.Api;
using Microsoft.CodeAnalysis.SolutionCrawler;

namespace Microsoft.CodeAnalysis.ExternalAccess.UnitTesting
{
    internal class UnitTestingIncrementalAnalyzerProvider : IIncrementalAnalyzerProvider
    {
        private readonly IUnitTestingIncrementalAnalyzerProviderImplementation _incrementalAnalyzerProvider;

        public UnitTestingIncrementalAnalyzerProvider(IUnitTestingIncrementalAnalyzerProviderImplementation incrementalAnalyzerProvider)
            => _incrementalAnalyzerProvider = incrementalAnalyzerProvider;

        public IIncrementalAnalyzer CreateIncrementalAnalyzer(Workspace workspace)
            => UnitTestingIncrementalAnalyzer.GetInstance(_incrementalAnalyzerProvider.CreateIncrementalAnalyzer(workspace));
    }
}
