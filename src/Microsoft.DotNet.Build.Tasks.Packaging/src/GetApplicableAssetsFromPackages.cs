// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Frameworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Microsoft.DotNet.Build.Tasks.Packaging
{
    public class GetApplicableAssetsFromPackages : PackagingTask
    {
        private Dictionary<string, List<PackageItem>> _packageToPackageItems;
        private Dictionary<string, PackageItem> _targetPathToPackageItem;
        private AggregateNuGetAssetResolver _resolver;

        /// <summary>
        /// All items that make up packages to resolve from.  
        /// Must have the following metadata
        ///     Identity - path to the file in the binary directory
        ///         TargetPath - path to the file in the package
        ///         PackageId - ID of the package containing the file
        /// </summary>
        [Required]
        public ITaskItem[] PackageAssets { get; set; }

        /// <summary>
        /// TargetMoniker to use when resolving assets.  EG: netcoreapp1.0, netstandard1.4
        /// </summary>
        [Required]
        public string TargetMoniker { get; set; }

        /// <summary>
        /// runtime.json that defines the RID graph
        /// </summary>
        [Required]
        public string RuntimeFile { get; set; }
        
        /// <summary>
        /// If specified will be used when resolving runtime assets, otherwise TargetMoniker will be used.
        /// </summary>
        public string RuntimeTargetMoniker { get; set; }

        /// <summary>
        /// If specified will be used when resolving runtime assets, otherwise no RID will be used.
        /// </summary>
        public string TargetRuntime { get; set; }

        [Output]
        public ITaskItem[] CompileAssets { get; set; }

        [Output]
        public ITaskItem[] RuntimeAssets { get; set; }

        /// <summary>
        /// Generates a table in markdown that lists the API version supported by 
        /// various packages at all levels of NETStandard.
        /// </summary>
        /// <returns></returns>
        public override bool Execute()
        {
            if (PackageAssets == null || PackageAssets.Length == 0)
            {
                Log.LogError("PackageAssets argument must be specified");
                return false;
            }

            if (String.IsNullOrEmpty(TargetMoniker))
            {
                Log.LogError("TargetMoniker argument must be specified");
                return false;
            }

            NuGetFramework fx = NuGetFramework.Parse(TargetMoniker);
            NuGetFramework runtimeFx = fx;

            if (!String.IsNullOrEmpty(RuntimeTargetMoniker))
            {
                runtimeFx = NuGetFramework.Parse(RuntimeTargetMoniker);
            }

            string targetString = String.IsNullOrEmpty(TargetRuntime) ? fx.ToString() : $"{fx}/{TargetRuntime}";

            LoadFiles();

            CompileAssets = _resolver.ResolveCompileAssets(fx)
                                .Where(ca => !NuGetAssetResolver.IsPlaceholder(ca))
                                .Select(ca => PackageItemAsResolvedAsset(_targetPathToPackageItem[ca]))
                                .ToArray();
            RuntimeAssets = _resolver.ResolveRuntimeAssets(runtimeFx, TargetRuntime)
                                .Where(ra => !NuGetAssetResolver.IsPlaceholder(ra))
                                .Select(ra => PackageItemAsResolvedAsset(_targetPathToPackageItem[ra]))
                                .ToArray();

            return !Log.HasLoggedErrors;
        }

        private void LoadFiles()
        {
            _packageToPackageItems = new Dictionary<string, List<PackageItem>>();
            foreach (var file in PackageAssets)
            {
                try
                {
                    var packageItem = new PackageItem(file);

                    if (String.IsNullOrWhiteSpace(packageItem.TargetPath))
                    {
                        Log.LogError($"{packageItem.TargetPath} is missing TargetPath metadata");
                    }

                    if (!_packageToPackageItems.ContainsKey(packageItem.Package))
                    {
                        _packageToPackageItems[packageItem.Package] = new List<PackageItem>();
                    }
                    _packageToPackageItems[packageItem.Package].Add(packageItem);
                }
                catch (Exception ex)
                {
                    Log.LogError($"Could not parse File {file.ItemSpec}. {ex}");
                    // skip it.
                }
            }

            // build a map to translate back to source file from resolved asset
            // we use package-specific paths since we're resolving a set of packages.
            _targetPathToPackageItem = new Dictionary<string, PackageItem>();
            foreach (var packageFiles in _packageToPackageItems)
            {
                foreach (PackageItem packageFile in packageFiles.Value)
                {
                    string packageSpecificTargetPath = AggregateNuGetAssetResolver.AsPackageSpecificTargetPath(packageFiles.Key, packageFile.TargetPath);

                    if (_targetPathToPackageItem.ContainsKey(packageSpecificTargetPath))
                    {
                        Log.LogError($"Files {_targetPathToPackageItem[packageSpecificTargetPath].SourcePath} and {packageFile.SourcePath} have the same TargetPath {packageSpecificTargetPath}.");
                    }
                    _targetPathToPackageItem[packageSpecificTargetPath] = packageFile;
                }
            }

            _resolver = new AggregateNuGetAssetResolver(RuntimeFile);
            foreach (string packageId in _packageToPackageItems.Keys)
            {
                _resolver.AddPackageItems(packageId, _packageToPackageItems[packageId].Select(f => f.TargetPath));
            }
        }

        private ITaskItem PackageItemAsResolvedAsset(PackageItem packageItem)
        {
            var item = new TaskItem(packageItem.OriginalItem);
            item.SetMetadata("Private", "false");
            item.SetMetadata("FromPkgProj", "true");
            item.SetMetadata("NuGetPackageId", packageItem.Package);
            item.SetMetadata("NuGetPackageVersion", packageItem.PackageVersion);
            return item;
        }
    }
}
