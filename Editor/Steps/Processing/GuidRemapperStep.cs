using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Nomnom.UnityProjectPatcher.Editor.Steps {
    // this will be a pain in my ass :)))))))))))
    public readonly struct GuidRemapperStep: IPatcherStep {
        //! used for the any next steps, before a recompile/restart, but isn't valid past a recompile/restart!
        public static AssetCatalogue AssetRipperCatalogue { get; set; }
        public static AssetCatalogue ProjectCatalogue { get; set; }

        public UniTask<StepResult> Run() {
            var settings = this.GetSettings();
            var arSettings = this.GetAssetRipperSettings();

            var arCatalogue = AssetScrubber.ScrubDiskFolder(arSettings.OutputExportFolderPath, arSettings.OutputExportAssetsFolderPath, arSettings.FoldersToExcludeFromRead);
            var arProjectSettingsCatalogue = AssetScrubber.ScrubDiskFolder(arSettings.OutputExportFolderPath, arSettings.OutputExportProjectSettingsFolderPath, arSettings.FoldersToExcludeFromRead);
            arCatalogue.InsertFrom(arProjectSettingsCatalogue);

            var projectCatalogue = AssetScrubber.ScrubProject();

            AssetRipperCatalogue = arCatalogue;
            ProjectCatalogue = projectCatalogue;

            var matches = projectCatalogue.CompareProjectToDisk(arCatalogue).ToArray();
            Debug.Log($"Found {matches.Length} matches");
            
            // okay so, this needs to take each match, swap to the new guid found
            // and then after all of this, replace all the old guids in the entire
            // asset ripper list with the new guid. this is slow as fuck... :/

            var allEntryMatches = new Dictionary<string, AssetCatalogue.Entry>();
            foreach (var match in matches) {
                if (string.IsNullOrEmpty(match.from.Guid)) continue;
                allEntryMatches[match.from.Guid] = match.to;
            }
            
            var stopWatch = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < matches.Length; i++) {
                var match = matches[i];
                var entryFrom = match.from;
                var entryTo = match.to;

                if (EditorUtility.DisplayCancelableProgressBar($"Guid Remapping [{i}/{matches.Length}]", $"Replacing {entryFrom.Guid} with {entryTo.Guid}", i / (float)matches.Length)) {
                    Debug.Log("Manually cancelled");
                    throw new OperationCanceledException();
                }
                
                // replace guids & write to disk
                try {
                    AssetScrubber.ReplaceMetaGuid(arCatalogue.RootPath, entryFrom, entryTo.Guid);
                    AssetScrubber.ReplaceAssetGuids(settings, arCatalogue.RootPath, entryFrom, allEntryMatches);
                    // AssetScrubber.ReplaceFileIds(arCatalogue.RootPath, entryFrom, matches);
                } catch (Exception e) {
                    Debug.LogError(e);
                }
            }
            
            Debug.Log($"guid match loop took {stopWatch.ElapsedMilliseconds}ms ({stopWatch.Elapsed.TotalSeconds}sec)");
            stopWatch.Restart();

            // var filesToExclude = arSettings.FilesToExcludeFromCopy;
            // var filesToExcludePrefix = filesToExclude.Where(x => x.EndsWith("*")).Select(x => x[..^1]).ToArray();
            // filesToExclude = filesToExclude.Except(filesToExcludePrefix).ToList();
            
            for (int i = 0; i < arCatalogue.Entries.Length; i++) {
                var entry = arCatalogue.Entries[i];

                if (EditorUtility.DisplayCancelableProgressBar($"Guid Remapping [{i}/{arCatalogue.Entries.Length}]", $"Checking associations for {entry.RelativePathToRoot}", i / (float)arCatalogue.Entries.Length)) {
                    Debug.Log("Manually cancelled");
                    throw new OperationCanceledException();
                }

                try {
                    AssetScrubber.ReplaceAssetGuids(settings, arCatalogue.RootPath, entry, allEntryMatches);
                    // AssetScrubber.ReplaceFileIds(arCatalogue.RootPath, entry, matches);
                } catch (Exception e) {
                    Debug.LogError(e);
                }
            }
            
            Debug.Log($"guid arCatalogue entries loop took {stopWatch.ElapsedMilliseconds}ms ({stopWatch.Elapsed.TotalSeconds}sec)");
            stopWatch.Stop();
            
            EditorUtility.ClearProgressBar();

            return UniTask.FromResult(StepResult.Success);
        }
        
        public void OnComplete(bool failed) { }
    }
}