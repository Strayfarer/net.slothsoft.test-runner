using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Constraints;
using NUnit.Framework.Internal;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityObject = UnityEngine.Object;

namespace Slothsoft.TestRunner.Editor.Validation.Internal {
    sealed class AssetValidator : IAssetValidator, IDisposable {
        internal IReadOnlyList<string> validAssetPaths = Array.Empty<string>();

        internal bool failImmediately = false;

        MethodInfo CurrentContext => currentContexts.Peek();

        readonly Stack<MethodInfo> currentContexts = new(new MethodInfo[] { default });

        MethodInfo lastContext = default;

        /// <inheritdoc/>
        public UnityObject currentAsset => CurrentAssetInfos.asset;

        /// <inheritdoc/>
        public string currentAssetPath => CurrentAssetInfos.assetPath;

        AssetInfo CurrentAssetInfos => currentAssetInfos.Peek();

        readonly Stack<AssetInfo> currentAssetInfos = new(new AssetInfo[] { new() });

        AssetInfo lastAssetInfo;

        readonly HashSet<UnityObject> validatedObjects = new();

        readonly ConcurrentQueue<string> failures = new();

        string mainAssetPath;

        bool printPackagePaths;

        /// <inheritdoc/>
        public void AssertFail(string message) {
            if (lastContext != CurrentContext || lastAssetInfo != CurrentAssetInfos) {
                lastContext = CurrentContext;
                lastAssetInfo = CurrentAssetInfos;
                if (lastContext is not null) {
                    message = $"# {lastContext.DeclaringType.Name} > {lastContext.Name} for: {GetName(lastAssetInfo.asset)}{Environment.NewLine}{message}";
                }
            }

            message = message.TrimEnd() + Environment.NewLine;

            if (failImmediately) {
                StopLogging();
                Assert.Fail(message);
            }

            failures.Enqueue(message);
        }

        /// <inheritdoc/>
        public void AssertTrue(bool assertion, string message) {
            AssertThat(assertion, Is.True, message);
        }

        /// <inheritdoc/>
        public void AssertFalse(bool assertion, string message) {
            AssertThat(assertion, Is.False, message);
        }

        /// <inheritdoc/>
        public void AssertThat(object actual, IResolveConstraint constraint) {
            AssertThat(actual, constraint, string.Empty);
        }

        /// <inheritdoc/>
        public void AssertThat(object actual, IResolveConstraint constraint, string message) {
            var result = constraint
                .Resolve()
                .ApplyTo(actual);

            if (!result.IsSuccess) {
                using TextMessageWriter writer = new();
                result.WriteMessageTo(writer);
                string fail = writer.ToString();
                if (!string.IsNullOrEmpty(message)) {
                    fail = message + Environment.NewLine + fail;
                }

                AssertFail(fail);
            }
        }

        /// <inheritdoc/>
        public void AssertDoesNotHaveComponent<TComponent>(GameObject obj) where TComponent : class {
#pragma warning disable UNT0014 // Invalid type for call to GetComponent
            AssertFalse(obj.TryGetComponent(out TComponent component), $"{GetName(obj)} was expected to NOT have a component {typeof(TComponent).FullName}, but did.\n{component}");
#pragma warning restore UNT0014 // Invalid type for call to GetComponent
        }

        /// <inheritdoc/>
        public bool AssertHasComponent<TComponent>(GameObject obj) where TComponent : class {
            return AssertHasComponent<TComponent>(obj, out _);
        }

        /// <inheritdoc/>
        public bool AssertHasComponent<TComponent>(GameObject obj, out TComponent component) where TComponent : class {
#pragma warning disable UNT0014 // Invalid type for call to GetComponent
            var components = obj.GetComponents<TComponent>();
#pragma warning restore UNT0014 // Invalid type for call to GetComponent
            AssertThat(components, Has.Length.EqualTo(1), $"{GetName(obj)} was expected to have exactly 1 component {typeof(TComponent).FullName}, but didn't.");
            component = components.FirstOrDefault();
            return components.Length == 1;
        }

        /// <inheritdoc/>
        public void AssertAssetPath(string assetPath, string message) {
            AssertThat(assetPath, AssetUtils.isNotDeprecatedAssetConstraint, $"Found a reference to a deprecated asset. Deprecated assets will get deleted soon, so references to them need to be removed or updated.{Environment.NewLine}  The offending asset is: {assetPath}");

            if (validAssetPaths is { Count: > 0 }) {
                var constraint = Is.SamePathOrUnder(validAssetPaths[0]);
                for (int i = 1; i < validAssetPaths.Count; i++) {
                    constraint = constraint.Or.SamePathOrUnder(validAssetPaths[i]);
                }

                int failureCount = failures.Count;

                AssertThat(assetPath, constraint, message);

                if (failureCount < failures.Count) {
                    printPackagePaths = true;
                }
            } else {
                // fallback: we don't know what's valid inside this package, but "Assets" definitely isn't
                AssertThat(assetPath, Is.Not.SamePathOrUnder("Assets/"), message);
            }
        }

        internal void AssertFailNow() {
            if (failures.Count > 0) {
                if (printPackagePaths) {
                    failures.Enqueue("Valid package paths are:");
                    foreach (string path in validAssetPaths) {
                        failures.Enqueue($" - {path}");
                    }
                }

                string message = string.Join(Environment.NewLine, failures);
                failures.Clear();
                StopLogging();
                Assert.Fail(message);
            }
        }

        /// <inheritdoc/>
        public void ValidateAsset(UnityObject asset) {
            string assetPath = AssetDatabase.GetAssetPath(asset);

            ValidateAsset(asset, assetPath);
        }

        /// <inheritdoc/>
        public void ValidateAsset(string assetPath) {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);

            ValidateAsset(asset, assetPath);
        }

        void ValidateAsset(UnityObject asset, string assetPath) {
            if (string.IsNullOrEmpty(assetPath)) {
                assetPath = null;
            }

            if (mainAssetPath is null) {
                mainAssetPath = assetPath;
            } else {
                AssertThat(assetPath, Is.Null.Or.EqualTo(mainAssetPath), $"{nameof(ValidateAsset)} should not be called for assets other than those at '{mainAssetPath}'!");
            }

            if (asset is BrokenPrefabAsset) {
                AssertFail($"Prefab {GetName(asset)} is broken!");
                return;
            }

            if (!asset) {
                if (assetPath is not null) {
                    AssertFail($"Failed to load asset at path: {assetPath}");
                }

                return;
            }

            if (!validatedObjects.Add(asset)) {
                return;
            }

            AssetInfo info = new() {
                asset = asset,
                assetPath = assetPath,
            };

            if (!string.IsNullOrEmpty(assetPath)) {
                info.isTestAsset = AssetUtils.IsTestAsset(assetPath);
                info.isWIPAsset = AssetUtils.IsWIPAsset(assetPath);
                info.isDeprecatedAsset = AssetUtils.IsDeprecatedAsset(assetPath);
            }

            currentAssetInfos.Push(info);
            InvokeValidators(info);
            currentAssetInfos.Pop();
        }

        /// <inheritdoc/>
        public Scene currentScene => currentScenes.Peek();

        readonly Stack<Scene> currentScenes = new(new[] { default(Scene) });

        /// <inheritdoc/>
        public bool CanOpenScene(string scenePath) {
            // https://discussions.unity.com/t/check-if-asset-inside-package-is-readonly/793326
            if (PackageInfo.FindForAssetPath(scenePath) is PackageInfo package) {
                return package.source is PackageSource.Embedded or PackageSource.Local;
            }

            return true;
        }

        /// <inheritdoc/>
        public void OpenScene(string scenePath) {
            currentScenes.Push(EditorSceneManager.OpenScene(scenePath, Application.isPlaying ? OpenSceneMode.Additive : OpenSceneMode.Single));
        }

        /// <inheritdoc/>
        public void CloseScene() {
            var scene = currentScenes.Pop();

            if (scene.IsValid()) {
                EditorSceneManager.CloseScene(scene, true);
            }
        }

        /// <inheritdoc/>
        public string GetName(UnityObject asset) {
            return asset switch {
#pragma warning disable UNT0029 // Pattern matching with null on Unity objects
                _ when asset is null => "NULL REFERENCE",
#pragma warning restore UNT0029 // Pattern matching with null on Unity objects
                _ when !asset => $"MISSING {asset.GetType().Name} REFERENCE",
                Component component => $"{asset.GetType().Name} {GetNameByHierarchy(component.transform)}",
                GameObject obj => $"{asset.GetType().Name} {GetNameByHierarchy(obj.transform)}",
                _ => $"{asset.GetType().Name} '{asset.name}'",
            };
        }

        string GetNameByHierarchy(Transform transform) {
            Stack<string> hierarchy = new();
            for (; transform; transform = transform.parent) {
                hierarchy.Push($"'{transform.name}'");
            }

            return string.Join(" > ", hierarchy);
        }

        void InvokeValidators(AssetInfo info) {
            var type = info.asset.GetType();

            if (!validators.TryGetValue(type, out var validates)) {
                validates = validators[type] = FindValidators(type)
                    .ToList();
            }

            foreach ((var method, var attribute) in validates.Where(validate => validate.attribute.CanValidate(info))) {
                object[] args = method.GetParameters().Length == 1
                    ? new object[] { info.asset }
                    : new object[] { info.asset, this };

                currentContexts.Push(method);

                try {
                    method.Invoke(null, args);
                } catch (TargetInvocationException exception) {
                    AssertFail(exception.InnerException.ToString());
                } finally {
                    currentContexts.Pop();
                }
            }
        }

        static readonly Dictionary<Type, List<(MethodInfo method, ValidateAttribute attribute)>> validators = new();

        static readonly List<(MethodInfo method, ValidateAttribute attribute, Type assetType)> attributes = ReflectionUtils
            .FindMethodsWithAttribute<ValidateAttribute>()
            .Select(validate => (validate.method, validate.attribute, validate.method.GetParameters()[0].ParameterType))
            .ToList();

        static IEnumerable<(MethodInfo method, ValidateAttribute attribute)> FindValidators(Type type) {
            return attributes
                .Where(validate => validate.assetType.IsAssignableFrom(type))
                .Select(validate => (validate.method, validate.attribute));
        }

        public AssetValidator() {
            if (Thread.CurrentThread.ManagedThreadId != 1) {
                throw new MethodAccessException("Validator must only be used on the main thread.");
            }

            StartLogging();
        }

        public void Dispose() {
            StopLogging();
        }

        static int globalLogCount = 0;

        bool isLogging = false;

        void StartLogging() {
            if (!isLogging) {
                isLogging = true;

                Application.logMessageReceivedThreaded += OnLogMessageReceived;

                var previous = Debug.unityLogger.filterLogType;
                Debug.unityLogger.filterLogType = LogType.Assert;
                LogAssert.ignoreFailingMessages = true;
                Debug.unityLogger.filterLogType = previous;

                globalLogCount++;
            }
        }

        void StopLogging() {
            if (isLogging) {
                isLogging = false;

                globalLogCount--;
                if (globalLogCount == 0) {
                    var previous = Debug.unityLogger.filterLogType;
                    Debug.unityLogger.filterLogType = LogType.Assert;
                    LogAssert.ignoreFailingMessages = false;
                    Debug.unityLogger.filterLogType = previous;
                }

                Application.logMessageReceivedThreaded -= OnLogMessageReceived;
            }
        }

        void OnLogMessageReceived(string condition, string stackTrace, LogType type) {
            if (type is LogType.Error or LogType.Assert or LogType.Exception) {
                AssertFail($"[{type}] {condition}{Environment.NewLine}{stackTrace}");
            }
        }
    }
}
