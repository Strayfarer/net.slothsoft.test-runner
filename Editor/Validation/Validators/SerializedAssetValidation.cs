using System;
using UnityEditor;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Slothsoft.TestRunner.Editor.Validation.Validators {
    static class SerializedAssetValidation {
        [Validate]
        public static void ValidateSerializedProperties(UnityObject asset, IAssetValidator validator) {
            try {
                using SerializedObject serialized = new(asset);
                using var property = serialized.GetIterator();

                while (property.NextVisible(true)) {
                    try {
                        ValidateSerializedProperty(property, validator);
                    } catch (Exception e) {
                        validator.AssertFail($"Failed to process property '{property.propertyPath}' of asset {validator.GetName(asset)}:" + Environment.NewLine + e);
                    }
                }
            } catch (Exception e) {
                validator.AssertFail($"Failed to create a {typeof(SerializedObject)} for asset {validator.GetName(asset)}:" + Environment.NewLine + e);
            }
        }

        internal static void ValidateSerializedProperty(SerializedProperty property, IAssetValidator validator) {
            switch (property) {
#if UNITY_6000_2_OR_NEWER
                case { propertyType: SerializedPropertyType.EntityId } when property.entityIdValue != EntityId.None:
#endif
                case { propertyType: SerializedPropertyType.ObjectReference, objectReferenceInstanceIDValue: not 0 }:
                    var target = property.serializedObject.targetObject;

                    if (!property.objectReferenceValue) {
                        validator.AssertFail($"{validator.GetName(target)} references a missing {GetType(property)} in property '{property.propertyPath}'!");
                    } else {
#if UNITY_6000_2_OR_NEWER
                        string path = property.propertyType is SerializedPropertyType.EntityId
                            ? AssetDatabase.GetAssetPath(property.entityIdValue)
                            : AssetDatabase.GetAssetPath(property.objectReferenceValue);
#else
                        string path = AssetDatabase.GetAssetPath(property.objectReferenceInstanceIDValue);
#endif
                        if (string.IsNullOrEmpty(path)) {
                            // references to assets without path are dubious, but could mean that we're inside a scene, or that the asset we're validating was created at runtime. Skip!
                        } else {
                            validator.AssertAssetPath(
                                path,
                                $"{validator.GetName(target)} references a {GetType(property)} NOT residing in any of its dependent packages in property '{property.propertyPath}'!{Environment.NewLine}  Either move the asset to the package, remove the reference to it, or update the package's dependencies to include the asset.{Environment.NewLine}  The offending asset is: {path}"
                            );
                        }
                    }

                    break;
            }
        }

        static string GetType(SerializedProperty property) {
            string type = property.type;

            if (type.StartsWith("PPtr<")) {
                type = type["PPtr<".Length..^1];
            }

            if (type.StartsWith("$")) {
                type = type["$".Length..];
            }

            return type;
        }
    }
}