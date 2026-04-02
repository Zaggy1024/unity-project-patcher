using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Nomnom.UnityProjectPatcher.Editor.Steps {
    /// <summary>
    /// This converts all InputActionAssets's internal data into its actual format,
    /// as the ripped data can't be seen in the editor, then assigns any preloaded
    /// InputActionAsset to the project-wide settings.
    /// </summary>
    public readonly struct FixInputSystemAssetsStep: IPatcherStep {
        private const string InputSettingsActionsKey = "com.unity.input.settings.actions";

        public UniTask<StepResult> Run() {
            var settings = this.GetSettings();
            var arSettings = this.GetAssetRipperSettings();

            if (!arSettings.TryGetFolderMapping("MonoBehaviour", out var monoBehaviourFolder, out var exclude) || exclude) {
                Debug.LogError("Could not find \"MonoBehaviour\" folder mapping");
                return UniTask.FromResult(StepResult.Failure);
            }

            // Read preloaded asset GUIDs from the exported ProjectSettings.
            // These GUIDs have been remapped by GuidRemapperStep and match project GUIDs.
            var preloadedGuids = GetPreloadedAssetGuids(arSettings);

            var inputActions = AssetDatabase.FindAssets("t:InputActionAsset", new [] {
                Path.Combine(settings.ProjectGameAssetsPath, monoBehaviourFolder).ToAssetDatabaseSafePath()
            });
            foreach (var guid in inputActions) {
                try {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);

                    // ? need this otherwise unity won't load up all the input data :/
                    var inputActionAssetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);

                    var asset = AssetDatabase.LoadAssetAtPath(assetPath, inputActionAssetType);
                    var clone = Object.Instantiate(asset);
                    var realPath = Path.GetFullPath(assetPath);
                    var text = File.ReadAllText(realPath.ToValidPath());
                    if (!text.Trim().StartsWith("%YAML")) continue;

                    clone.name = $"{Path.GetFileNameWithoutExtension(realPath)}";

                    var json = Fix(clone);
                    var newPath = Path.Combine(Path.GetDirectoryName(realPath), $"{Path.GetFileNameWithoutExtension(realPath)}.inputactions");
                    File.WriteAllText(newPath.ToValidPath(), json);

                    AssetDatabase.Refresh();

#if UNITY_2020_3_OR_NEWER
                    var localNewPath = Path.GetRelativePath(Path.Combine(Application.dataPath, ".."), newPath);
#else
                    var localNewPath = PathNetCore.GetRelativePath(Path.Combine(Application.dataPath, ".."), newPath);
#endif

                    var newObj = AssetDatabase.LoadAssetAtPath(localNewPath, inputActionAssetType);

                    // If this input action asset was in the game's preloaded assets,
                    // assign it as the project-wide InputSystem actions asset.
                    if (preloadedGuids.Contains(guid)) {
                        Debug.Log($"Assigning \"{clone.name}\" as project-wide InputSystem actions asset");
                        EditorBuildSettings.AddConfigObject(InputSettingsActionsKey, newObj, false);
                    }
                } catch (Exception e) {
                    Debug.LogError(e);
                }
            }

            return UniTask.FromResult(StepResult.Success);
        }

        public void OnComplete(bool failed) { }

        private static System.Collections.Generic.HashSet<string> GetPreloadedAssetGuids(Nomnom.UnityProjectPatcher.AssetRipper.AssetRipperSettings arSettings) {
            var guids = new System.Collections.Generic.HashSet<string>();
            var projectSettingsPath = Path.Combine(arSettings.OutputExportFolderPath, "ProjectSettings", "ProjectSettings.asset");
            if (!File.Exists(projectSettingsPath)) {
                Debug.LogWarning($"Could not find exported ProjectSettings.asset at {projectSettingsPath}");
                return guids;
            }

            var text = File.ReadAllText(projectSettingsPath);
            var sectionMatch = Regex.Match(text, @"preloadedAssets:(.*?)(?=\n  \w)", RegexOptions.Singleline);
            if (!sectionMatch.Success) return guids;

            foreach (Match guidMatch in Regex.Matches(sectionMatch.Value, @"guid:\s*([0-9a-fA-F]+)")) {
                guids.Add(guidMatch.Groups[1].Value);
            }

            Debug.Log($"Found {guids.Count} preloaded asset GUIDs from exported ProjectSettings");
            return guids;
        }

        private string Fix(UnityEngine.Object clone) {
            // some terrible string manipulation to fix the json, but idc it works
            var json = JsonUtility.ToJson(clone, true);
            var lines = json.Split('\n').ToList();
            lines.Insert(1, $"  \"m_Name\": \"{clone.name}\",");
            json = string.Join("\n", lines);
            foreach (var group in Regex.Matches(json, @"""(m_[^""]*)""").Cast<Match>().ToArray()) {
                var value = group.Value.Replace("m_", string.Empty);
                var charArray = value.ToCharArray();
                charArray[1] = char.ToLower(charArray[1]);
                value = new string(charArray);
                json = json.Replace(group.Value, value);
            }
            json = json.Replace("\"actionMaps\"", "\"maps\"");

            var jsonObj = JObject.Parse(json);
            var maps = jsonObj["maps"];

            foreach (JObject map in maps) {
                var actions = map["actions"];
                var assetProperty = map.Property("asset");
                assetProperty?.Remove();

                foreach (JObject action in actions) {
                    var newAction = new JObject();
                    var flags = action["flags"].Value<int>();
                    action["initialStateCheck"] = flags == 1;

                    var type = action["type"].Value<int>();
                    switch (type) {
                        case 0:
                            newAction["type"] = "Value";
                            break;
                        case 1:
                            newAction["type"] = "Button";
                            break;
                        case 2:
                            newAction["type"] = "PassThrough";
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    var flagProperty = action.Property("flags");
                    flagProperty?.Remove();

                    var singletonActionBindingsProperty = action.Property("singletonActionBindings");
                    singletonActionBindingsProperty?.Remove();
                }

                var bindings = map["bindings"];
                foreach (JObject binding in bindings) {
                    var flags = binding["flags"].Value<int>();
                    binding["isComposite"] = (flags & 4) == 4;
                    binding["isPartOfComposite"] = (flags & 8) == 8;

                    var flagProperty = binding.Property("flags");
                    flagProperty?.Remove();
                }
            }

            var controlSchemes = jsonObj["controlSchemes"];
            foreach (JObject controlScheme in controlSchemes) {
                var devices = new JArray();
                var deviceRequirements = controlScheme["deviceRequirements"];
                foreach (JObject deviceRequirement in deviceRequirements) {
                    deviceRequirement["devicePath"] = deviceRequirement["controlPath"];

                    var flags = deviceRequirement["flags"].Value<int>();
                    deviceRequirement["isOptional"] = (flags & 1) == 1;
                    deviceRequirement["isOR"] = (flags & 2) == 2;

                    var flagProperty = deviceRequirement.Property("flags");
                    flagProperty?.Remove();

                    var controlPathProperty = deviceRequirement.Property("controlPath");
                    controlPathProperty?.Remove();

                    devices.Add(deviceRequirement);
                }

                var deviceRequirementsProperty = controlScheme.Property("deviceRequirements");
                deviceRequirementsProperty?.Remove();

                controlScheme["devices"] = devices;
            }

            json = jsonObj.ToString(Formatting.Indented);
            return json;
        }
    }
}