using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Slothsoft.TestRunner.Editor.Validation.Validators {
    static class ShaderValidation {
        static readonly MethodInfo OpenCompiledShaderMethod = typeof(ShaderUtil).GetMethod("OpenCompiledShader", BindingFlags.NonPublic | BindingFlags.Static);

        static void OpenCompiledShader(Shader shader, int mode, int externPlatformsMask, bool includeAllVariants, bool preprocessOnly, bool stripLineDirectives) {
            OpenCompiledShaderMethod.Invoke(null, new object[] { shader, mode, externPlatformsMask, includeAllVariants, preprocessOnly, stripLineDirectives });
        }

        static readonly MethodInfo CompileShaderVariantMethod = typeof(ShaderUtil).GetMethod("CompileShaderVariant", BindingFlags.NonPublic | BindingFlags.Static);

        static ShaderData.VariantCompileInfo CompileShaderVariant(Shader shader, int subShaderIndex, int passId, ShaderType shaderType, BuiltinShaderDefine[] platformKeywords, string[] keywords, ShaderCompilerPlatform shaderCompilerPlatform, BuildTarget buildTarget, GraphicsTier tier, bool outputForExternalTool) {
            return (ShaderData.VariantCompileInfo)CompileShaderVariantMethod.Invoke(null, new object[] { shader, subShaderIndex, passId, shaderType, platformKeywords, keywords, shaderCompilerPlatform, buildTarget, tier, outputForExternalTool });
        }

        static readonly BuildTarget ActiveCompileTarget = EditorUserBuildSettings.activeBuildTarget;

        static readonly ShaderCompilerPlatform[] ActiveCompilePlatforms = PlayerSettings
            .GetGraphicsAPIs(ActiveCompileTarget)
            .Select(graphicsAPI => graphicsAPI switch {
                GraphicsDeviceType.Direct3D11 => ShaderCompilerPlatform.D3D,
                GraphicsDeviceType.Direct3D12 => ShaderCompilerPlatform.D3D,
                GraphicsDeviceType.Vulkan => ShaderCompilerPlatform.Vulkan,
                GraphicsDeviceType.Metal => ShaderCompilerPlatform.Metal,
                _ => ShaderCompilerPlatform.None,
            })
            .Where(platform => platform is not ShaderCompilerPlatform.None)
            .Distinct()
            .ToArray();

        static readonly int ActiveCompilePlatformsMask = ActiveCompilePlatforms
            .Aggregate(0, (mask, platform) => mask | (1 << (int)platform));

        const bool INCLUDE_ALL_VARIANTS = false;
        const bool PREPROCESS_ONLY = false;
        const bool STRIP_LINE_DIRECTIVES = false;

        public static bool recompileShaders = false;
        public static bool openCompiledShaders = false;

        [Validate]
        public static void CompileShader(Shader shader, IAssetValidator validator) {
            if (recompileShaders || Application.isBatchMode) {
                if (openCompiledShaders) {
                    OpenCompiledShader(shader, 1, ActiveCompilePlatformsMask, INCLUDE_ALL_VARIANTS, PREPROCESS_ONLY, STRIP_LINE_DIRECTIVES);
                }
            } else {
                ReportToValidator(ShaderUtil.GetShaderMessages(shader), validator);
            }
        }

        static void ReportToValidator(ShaderMessage[] messages, IAssetValidator validator) {
            foreach (var message in messages) {
                switch (message.severity) {
                    case ShaderCompilerMessageSeverity.Error:
                        validator.AssertFail($"{message.file}:{message.line}{Environment.NewLine}[{message.severity}] {message.message}{Environment.NewLine}{message.messageDetails}");
                        break;
                }
            }
        }
    }
}