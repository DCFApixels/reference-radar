using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Bro.ReferenceRadar
{
    public class ReferenceWindow : EditorWindow
    {
        private static readonly string[] ModeLabels = { "Used By", "Uses" };
        private int _mode;
        [SerializeField] private AssetKind _assetFilter = AssetKind.All;
        [SerializeField] private SortMode _sortMode;
        [SerializeField] private string _nameSearch = string.Empty;
        [NonSerialized] private SearchField _searchField;
        private Vector2 _scrollPosition;
        private readonly List<ResultEntry> _results = new();
        private readonly List<ResultEntry> _visibleResults = new();
        private string[] _selectedGuids = Array.Empty<string>();
        [NonSerialized] private GUIStyle _rowStyle;
        private double _lastCacheCheckTime;
        
        private bool _isSceneSelection;
        private GameObject _selectedGameObject;
        private readonly List<SceneResultEntry> _sceneResults = new();
        private readonly List<SceneResultEntry> _visibleSceneResults = new();

        private enum SortMode
        {
            Name,
            Type
        }

        // Categories are in alphabetical display order, with Other last.
        [Flags]
        private enum AssetKind
        {
            Animation = 1 << 0,
            Audio = 1 << 1,
            Folder = 1 << 2,
            Font = 1 << 3,
            Material = 1 << 4,
            Mesh = 1 << 5,
            Model = 1 << 6,
            Prefab = 1 << 7,
            Scene = 1 << 8,
            Script = 1 << 9,
            ScriptableObject = 1 << 10,
            Shader = 1 << 11,
            Text = 1 << 12,
            Texture = 1 << 13,
            Other = 1 << 14,
            All = -1
        }

        private readonly struct ResultEntry
        {
            public readonly string Path;
            public readonly string FileName;
            public readonly AssetKind Kind;

            public ResultEntry(string path)
            {
                Path = path;
                FileName = System.IO.Path.GetFileName(path);
                Kind = GetAssetKind(path);
            }
        }

        private struct SceneResultEntry
        {
            public GameObject GameObject;
            public string ComponentName;
        }

        [MenuItem("Window/ReferenceRadar")]
        private static void OpenWindow()
        {
            GetWindow<ReferenceWindow>("ReferenceRadar");
        }

        private void OnEnable()
        {
            ProjectOverlay.CacheUpdated -= OnCacheUpdated;
            ProjectOverlay.CacheUpdated += OnCacheUpdated;

            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneOpened += OnSceneOpened;

            EditorSceneManager.sceneClosed -= OnSceneClosed;
            EditorSceneManager.sceneClosed += OnSceneClosed;

            Undo.undoRedoPerformed -= InvalidateSceneMap;
            Undo.undoRedoPerformed += InvalidateSceneMap;

            EditorApplication.hierarchyChanged -= InvalidateSceneMap;
            EditorApplication.hierarchyChanged += InvalidateSceneMap;

            OnSelectionChange();
        }

        private void OnDisable()
        {
            ProjectOverlay.CacheUpdated -= OnCacheUpdated;
            EditorSceneManager.sceneOpened -= OnSceneOpened;
            EditorSceneManager.sceneClosed -= OnSceneClosed;
            Undo.undoRedoPerformed -= InvalidateSceneMap;
            EditorApplication.hierarchyChanged -= InvalidateSceneMap;
        }

        private void OnCacheUpdated()
        {
            RebuildResults();
            Repaint();
        }

        private void OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            InvalidateSceneMap();
        }

        private void OnSceneClosed(Scene scene)
        {
            InvalidateSceneMap();
        }

        private void InvalidateSceneMap()
        {
            if (_isSceneSelection)
            {
                RebuildResults();
            }

            Repaint();
        }

        private void OnSelectionChange()
        {
            var activeGo = Selection.activeGameObject;
            var isSceneObject = activeGo != null && activeGo.scene.IsValid() && !EditorUtility.IsPersistent(activeGo);
            if (isSceneObject)
            {
                _isSceneSelection = true;
                _selectedGameObject = activeGo;
                _selectedGuids = Array.Empty<string>();
                RebuildResults();
                Repaint();
                return;
            }
            _isSceneSelection = false;
            _selectedGameObject = null;
            _selectedGuids = Selection.assetGUIDs ?? Array.Empty<string>();
            RebuildResults();
            Repaint();
        }

        private void RebuildResults()
        {
            _results.Clear();
            _sceneResults.Clear();
            if (_isSceneSelection)
            {
                RebuildSceneObjectResults();
            }
            else if (ProjectOverlay.IsReady)
            {
                var cache = ProjectOverlay.Cache;
                var seen = new HashSet<string>(_selectedGuids);
                if (_mode == 0)
                {
                    CollectUsedBy(cache, seen);
                }
                else
                {
                    CollectUses(cache, seen);
                }
            }
            UpdateVisibleResults();
        }

        private void UpdateVisibleResults()
        {
            var query = _nameSearch?.Trim();
            _visibleResults.Clear();
            foreach (var result in _results)
            {
                if ((_assetFilter & result.Kind) != 0 && MatchesName(result.FileName, query))
                {
                    _visibleResults.Add(result);
                }
            }
            _visibleResults.Sort(_sortMode == SortMode.Type ? CompareByType : CompareByName);

            _visibleSceneResults.Clear();
            foreach (var result in _sceneResults)
            {
                if (result.GameObject != null && MatchesName(result.GameObject.name, query))
                {
                    _visibleSceneResults.Add(result);
                }
            }
        }

        private static bool MatchesName(string name, string query)
        {
            return string.IsNullOrEmpty(query) || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int CompareByName(ResultEntry a, ResultEntry b)
        {
            var nameOrder = StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName);
            return nameOrder != 0 ? nameOrder : string.CompareOrdinal(a.Path, b.Path);
        }

        private static int CompareByType(ResultEntry a, ResultEntry b)
        {
            var typeOrder = ((int)a.Kind).CompareTo((int)b.Kind);
            return typeOrder != 0 ? typeOrder : CompareByName(a, b);
        }

        private static AssetKind GetAssetKind(string path)
        {
            var type = AssetDatabase.GetMainAssetTypeAtPath(path);
            if (type == typeof(GameObject))
            {
                return AssetImporter.GetAtPath(path) is ModelImporter ? AssetKind.Model : AssetKind.Prefab;
            }
            if (type == typeof(DefaultAsset) && AssetDatabase.IsValidFolder(path)) return AssetKind.Folder;
            if (type == typeof(MonoScript)) return AssetKind.Script;
            if (type == typeof(SceneAsset)) return AssetKind.Scene;
            if (type == typeof(Shader) || type == typeof(ComputeShader)) return AssetKind.Shader;
            if (type == typeof(Sprite) || typeof(Texture).IsAssignableFrom(type)) return AssetKind.Texture;
            if (type == typeof(Material)) return AssetKind.Material;
            if (type == typeof(AnimationClip) || typeof(RuntimeAnimatorController).IsAssignableFrom(type))
            {
                return AssetKind.Animation;
            }
            if (type == typeof(AudioClip) || type == typeof(UnityEngine.Audio.AudioMixer)) return AssetKind.Audio;
            if (type == typeof(Font)) return AssetKind.Font;
            if (type == typeof(Mesh)) return AssetKind.Mesh;
            if (typeof(ScriptableObject).IsAssignableFrom(type)) return AssetKind.ScriptableObject;
            if (type == typeof(TextAsset)) return AssetKind.Text;
            return AssetKind.Other;
        }

        private void RebuildSceneObjectResults()
        {
            if (_selectedGameObject == null)
            {
                return;
            }

            _sceneResults.Add(new SceneResultEntry
            {
                GameObject = _selectedGameObject,
                ComponentName = "Self"
            });
            if (_mode == 1)
            {
                // Uses: asset references
                var assetPaths = new List<string>();
                SceneScanner.ScanGameObject(_selectedGameObject, assetPaths);
                foreach (var path in assetPaths)
                {
                    var guid = AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid))
                    {
                        continue;
                    }
                    _results.Add(new ResultEntry(path));
                }
                // Uses: scene object references
                var sceneRefs = SceneScanner.ScanGameObjectSceneRefs(_selectedGameObject);
                foreach (var sceneRef in sceneRefs)
                {
                    _sceneResults.Add(new SceneResultEntry
                    {
                        GameObject = sceneRef.GameObject,
                        ComponentName = sceneRef.ComponentName
                    });
                }
            }
            else
            {
                // Used By: find other scene objects that reference this GO
                var refs = SceneScanner.FindReferencesToObject(_selectedGameObject);
                foreach (var sceneRef in refs)
                {
                    _sceneResults.Add(new SceneResultEntry
                    {
                        GameObject = sceneRef.GameObject,
                        ComponentName = sceneRef.ComponentName
                    });
                }
            }
        }

        private void CollectUsedBy(Cache cache, HashSet<string> seen)
        {
            foreach (var selectedGuid in _selectedGuids)
            {
                var entry = cache.GetEntry(selectedGuid);
                if (entry == null)
                {
                    continue;
                }
                foreach (var kvp in entry.UsedByMap)
                {
                    if (!seen.Add(kvp.Key))
                    {
                        continue;
                    }
                    var path = AssetDatabase.GUIDToAssetPath(kvp.Key);
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }
                    _results.Add(new ResultEntry(path));
                }
            }
        }

        private void CollectUses(Cache cache, HashSet<string> seen)
        {
            foreach (var selectedGuid in _selectedGuids)
            {
                var entry = cache.GetEntry(selectedGuid);
                if (entry == null)
                {
                    continue;
                }
                foreach (var guid in entry.UseGuids)
                {
                    if (!seen.Add(guid))
                    {
                        continue;
                    }
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(path))
                    {
                        continue;
                    }
                    _results.Add(new ResultEntry(path));
                }
            }
        }

        private void OnGUI()
        {
            var time = EditorApplication.timeSinceStartup;
            if (time - _lastCacheCheckTime > 2.0)
            {
                _lastCacheCheckTime = time;
                ProjectOverlay.CheckCacheFile();
            }
            
            if (!ProjectOverlay.IsReady)
            {
                if (ProjectOverlay.IsScanning)
                {
                    EditorGUILayout.HelpBox("Scanning project...", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("Cache is not ready. Run a scan first.", MessageType.Info);
                    if (GUILayout.Button("Scan Project"))
                    {
                        PerformScan(false);
                    }
                }
                return;
            }
            
            DrawToolbar();
            DrawSelectionHeader();
       
            if (_isSceneSelection)
            {
                if (_selectedGameObject == null)
                {
                    EditorGUILayout.HelpBox("Selected scene object was destroyed.", MessageType.Warning);
                    return;
                }
                EnsureStyles();
                _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
                DrawFilterEmptyState();
                if (_visibleSceneResults.Count > 0)
                {
                    EditorGUILayout.LabelField("Scene References", EditorStyles.boldLabel);
                    foreach (var result in _visibleSceneResults)
                    {
                        if (result.GameObject == null)
                        {
                            continue;
                        }
                        DrawSceneResultRow(result);
                    }
                }
                if (_mode == 1 && _visibleResults.Count > 0)
                {
                    EditorGUILayout.Space(4);
                    EditorGUILayout.LabelField("Asset References", EditorStyles.boldLabel);
                    foreach (var result in _visibleResults)
                    {
                        DrawResultRow(result);
                    }
                }
                EditorGUILayout.EndScrollView();
                DrawFooter();
                return;
            }
            if (_selectedGuids.Length == 0)
            {
                EditorGUILayout.HelpBox("Select an asset in the Project window or an object in the Scene.", MessageType.Info);
                DrawFooter();
                return;
            }
            DrawResults();
            DrawFooter();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.Space(2);
            var newMode = GUILayout.Toolbar(_mode, ModeLabels);
            if (newMode != _mode)
            {
                _mode = newMode;
                RebuildResults();
            }

            AssetKind filter;
            SortMode sort;
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                GUILayout.Label("Types", EditorStyles.miniLabel, GUILayout.Width(34));
                filter = (AssetKind)EditorGUILayout.EnumFlagsField(_assetFilter, EditorStyles.toolbarPopup, GUILayout.ExpandWidth(true));
                GUILayout.Label("Sort", EditorStyles.miniLabel, GUILayout.Width(25));
                sort = (SortMode)EditorGUILayout.EnumPopup(_sortMode, EditorStyles.toolbarPopup, GUILayout.Width(62));
            }

            _searchField ??= new SearchField();
            var search = _searchField.OnToolbarGUI(_nameSearch ?? string.Empty);
            if (filter != _assetFilter || sort != _sortMode || search != _nameSearch)
            {
                _assetFilter = filter;
                _sortMode = sort;
                _nameSearch = search;
                _scrollPosition = Vector2.zero;
                UpdateVisibleResults();
            }
            EditorGUILayout.Space(2);
        }

        private void DrawSelectionHeader()
        {
            if (_isSceneSelection)
            {
                if (_selectedGameObject != null)
                {
                    EditorGUILayout.LabelField("Selected (Scene)", _selectedGameObject.name, EditorStyles.boldLabel);
                    if (Event.current.type == EventType.MouseDown && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                    {
                        EditorGUIUtility.PingObject(_selectedGameObject);
                        Selection.activeGameObject = _selectedGameObject;
                        Event.current.Use();
                    }
                }
                return;
            }
            
            if (_selectedGuids.Length == 0)
            {
                return;
            }
            
            if (_selectedGuids.Length == 1)
            {
                var filePath = AssetDatabase.GUIDToAssetPath(_selectedGuids[0]);
                var fileName = Path.GetFileName(filePath);
                EditorGUILayout.LabelField("Selected", fileName, EditorStyles.boldLabel);
                if (Event.current.type == EventType.MouseDown && GUILayoutUtility.GetLastRect().Contains(Event.current.mousePosition))
                {
                    var asset = AssetDatabase.LoadMainAssetAtPath(filePath);
                    if (asset != null)
                    {
                        EditorGUIUtility.PingObject(asset);
                        Selection.activeObject = asset;
                    }
                    Event.current.Use();
                }
            }
            else
            {
                EditorGUILayout.LabelField("Selected", $"{_selectedGuids.Length} assets", EditorStyles.boldLabel);
            }
        }

        private void DrawSceneResultRow(SceneResultEntry result)
        {
            var label = $" {result.GameObject.name}  ({result.ComponentName})";
            var icon = EditorGUIUtility.ObjectContent(result.GameObject, typeof(GameObject)).image;
            var content = new GUIContent(label, icon, GetGameObjectPath(result.GameObject));
            if (GUILayout.Button(content, _rowStyle, GUILayout.Height(20)))
            {
                EditorGUIUtility.PingObject(result.GameObject);
            }
        }

        private static string GetGameObjectPath(GameObject go)
        {
            var path = go.name;
            var parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        private void DrawResults()
        {
            EnsureStyles();
            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            const float rowHeight = 20f;
            var total = _visibleResults.Count;
            DrawFilterEmptyState();
            if (total > 0)
            {
                var viewHeight = position.height;
                var firstVisible = Mathf.Clamp(Mathf.FloorToInt(_scrollPosition.y / rowHeight), 0, total - 1);
                var lastVisible = Mathf.Clamp(Mathf.CeilToInt((_scrollPosition.y + viewHeight) / rowHeight), 0, total - 1);
                if (firstVisible > 0)
                {
                    GUILayout.Space(firstVisible * rowHeight);
                }
                for (var i = firstVisible; i <= lastVisible; i++)
                {
                    DrawResultRow(_visibleResults[i]);
                }
                var remaining = total - lastVisible - 1;
                if (remaining > 0)
                {
                    GUILayout.Space(remaining * rowHeight);
                }
            }
            EditorGUILayout.EndScrollView();
        }

        private void DrawFilterEmptyState()
        {
            if (_results.Count + _sceneResults.Count > 0 && _visibleResults.Count + _visibleSceneResults.Count == 0)
            {
                EditorGUILayout.HelpBox("No references match the current filters.", MessageType.Info);
            }
        }

        private void DrawResultRow(ResultEntry result)
        {
            var icon = AssetDatabase.GetCachedIcon(result.Path);
            var content = new GUIContent(" " + result.FileName, icon, result.Path);
            if (GUILayout.Button(content, _rowStyle, GUILayout.Height(20)))
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(result.Path);
                if (asset != null)
                {
                    EditorGUIUtility.PingObject(asset);
                }
            }
        }

        private void DrawFooter()
        {
            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            
            var assetCount = FormatResultCount(_visibleResults.Count, _results.Count);
            var label = _sceneResults.Count > 0
                ? $"{assetCount} project, {FormatResultCount(_visibleSceneResults.Count, _sceneResults.Count)} scene references"
                : $"{assetCount} references";
            
            EditorGUILayout.LabelField(label, EditorStyles.miniLabel);
            var settingsIcon = EditorGUIUtility.IconContent("d_Settings");
            settingsIcon.tooltip = "Settings";
            if (GUILayout.Button(settingsIcon, EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(18)))
            {
                SelectOrCreateSettings();
            }
            var refreshIcon = EditorGUIUtility.IconContent("d_Refresh");
            refreshIcon.tooltip = "Rescan Project";
            if (GUILayout.Button(refreshIcon, EditorStyles.miniButton, GUILayout.Width(24), GUILayout.Height(18)))
            {
                PerformScan(false);
            }
            EditorGUILayout.EndHorizontal();
        }

        private static string FormatResultCount(int visible, int total)
        {
            return visible == total ? total.ToString() : $"{visible}/{total}";
        }

        private static void SelectOrCreateSettings()
        {
            var settings = Settings.Instance;
            if (settings == null)
            {
                settings = CreateInstance<Settings>();
                AssetDatabase.CreateAsset(settings, "Assets/ReferenceRadarSettings.asset");
                AssetDatabase.SaveAssets();
                Settings.ResetSearch();
            }
            EditorGUIUtility.PingObject(settings);
            Selection.activeObject = settings;
        }

        private void PerformScan(bool isForce)
        {
            ProjectOverlay.StartFullScan(isForce);
        }

        private void EnsureStyles()
        {
            if (_rowStyle != null)
            {
                return;
            }
            
            var isProSkin = EditorGUIUtility.isProSkin;
            var textColor = isProSkin
                ? new Color(0.82f, 0.82f, 0.82f, 1f)
                : new Color(0.02f, 0.02f, 0.02f, 1f);
         
            _rowStyle = new GUIStyle
            {
                alignment = TextAnchor.MiddleLeft,
                imagePosition = ImagePosition.ImageLeft,
                fixedHeight = 0,
                padding = new RectOffset(4, 4, 2, 2),
                font = EditorStyles.label.font,
                fontSize = EditorStyles.label.fontSize,
            };
            
            _rowStyle.normal.textColor = textColor;
            _rowStyle.hover.textColor = textColor;
            _rowStyle.active.textColor = textColor;
            _rowStyle.focused.textColor = textColor;
            _rowStyle.onNormal.textColor = textColor;
            _rowStyle.onHover.textColor = textColor;
            _rowStyle.onActive.textColor = textColor;
            _rowStyle.onFocused.textColor = textColor;
        }
    }
}
