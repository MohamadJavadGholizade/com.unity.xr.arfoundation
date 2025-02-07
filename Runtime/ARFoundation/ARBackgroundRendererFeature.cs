using UnityEngine;
using UnityEngine.Rendering;
#if MODULE_URP_ENABLED
using System;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR.ARSubsystems;
#else
using ScriptableRendererFeature = UnityEngine.ScriptableObject;
#endif

namespace UnityEngine.XR.ARFoundation
{
    /// <summary>
    /// A render feature for rendering the camera background for AR devices.
    /// </summary>
    public class ARBackgroundRendererFeature : ScriptableRendererFeature
    {
#if MODULE_URP_ENABLED
        /// <summary>
        /// The scriptable render pass to be added to the renderer when the camera background is to be rendered.
        /// </summary>
        ARCameraBeforeOpaquesRenderPass beforeOpaquesScriptablePass => m_BeforeOpaquesScriptablePass ??= new ARCameraBeforeOpaquesRenderPass();
        ARCameraBeforeOpaquesRenderPass m_BeforeOpaquesScriptablePass;

        /// <summary>
        /// The scriptable render pass to be added to the renderer when the camera background is to be rendered.
        /// </summary>
        ARCameraAfterOpaquesRenderPass afterOpaquesScriptablePass => m_AfterOpaquesScriptablePass ??= new ARCameraAfterOpaquesRenderPass();
        ARCameraAfterOpaquesRenderPass m_AfterOpaquesScriptablePass;

        /// <summary>
        /// Create the scriptable render pass.
        /// </summary>
        public override void Create() {}

        /// <summary>
        /// Add the background rendering pass when rendering a game camera with an enabled AR camera background component.
        /// </summary>
        /// <param name="renderer">The scriptable renderer in which to enqueue the render pass.</param>
        /// <param name="renderingData">Additional rendering data about the current state of rendering.</param>
        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            var currentCamera = renderingData.cameraData.camera;
            if ((currentCamera != null) && (currentCamera.cameraType == CameraType.Game))
            {
                var cameraBackground = currentCamera.gameObject.GetComponent<ARCameraBackground>();
                if ((cameraBackground != null) && cameraBackground.backgroundRenderingEnabled
                    && (cameraBackground.material != null)
                    && TrySelectRenderPassForBackgroundRenderMode(cameraBackground.currentRenderingMode, out var renderPass))
                {
                    var invertCulling = cameraBackground.GetComponent<ARCameraManager>()?.subsystem?.invertCulling ?? false;
                    renderPass.Setup(cameraBackground, invertCulling);
                    renderer.EnqueuePass(renderPass);
                }
            }
        }

        /// <summary>
        /// Selects the render pass for a given <see cref="UnityEngine.XR.ARSubsystems.XRCameraBackgroundRenderingMode"/>
        /// </summary>
        /// <param name="renderingMode">The <see cref="UnityEngine.XR.ARSubsystems.XRCameraBackgroundRenderingMode"/>
        /// that indicates which render pass to use.
        /// </param>
        /// <param name="renderPass">The <see cref="ARCameraBackgroundRenderPass"/> that corresponds
        /// to the given <paramref name="renderingMode">.
        /// </param>
        /// <returns>
        /// <c>true</c> if <paramref name="renderPass"/> was populated. Otherwise, <c>false</c>.
        /// </returns>
        bool TrySelectRenderPassForBackgroundRenderMode(XRCameraBackgroundRenderingMode renderingMode, out ARCameraBackgroundRenderPass renderPass)
        {
            switch (renderingMode)
            {
                case XRCameraBackgroundRenderingMode.AfterOpaques:
                    renderPass = afterOpaquesScriptablePass;
                    return true;

                case XRCameraBackgroundRenderingMode.BeforeOpaques:
                    renderPass = beforeOpaquesScriptablePass;
                    return true;

                case XRCameraBackgroundRenderingMode.None:
                default:
                    renderPass = null;
                    return false;
            }
        }

        /// <summary>
        /// An abstract <see cref="ScriptableRenderPass"/> that provides common utilities for rendering an AR Camera Background.
        /// </summary>
        abstract class ARCameraBackgroundRenderPass : ScriptableRenderPass
        {
            /// <summary>
            /// The name for the custom render pass which will display in graphics debugging tools.
            /// </summary>
            const string k_CustomRenderPassName = "AR Background Pass (URP)";

            /// <summary>
            /// The material used for rendering the device background using the camera video texture and potentially
            /// other device-specific properties and textures.
            /// </summary>
            Material m_BackgroundMaterial;

            XRCameraBackgroundRenderingParams m_CameraBackgroundRenderingParams;

            /// <summary>
            /// Whether the culling mode should be inverted.
            /// ([CommandBuffer.SetInvertCulling](https://docs.unity3d.com/ScriptReference/Rendering.CommandBuffer.SetInvertCulling.html)).
            /// </summary>
            bool m_InvertCulling;

            /// <summary>
            /// The data that is used in both RenderGraph and non-RenderGraph paths.
            /// </summary>
            PassData m_PassData = new PassData();

            /// <summary>
            /// The default platform rendering parameters for the camera background.
            /// </summary>
            XRCameraBackgroundRenderingParams defaultCameraBackgroundRenderingParams
                => ARCameraBackgroundRenderingUtils.SelectDefaultBackgroundRenderParametersForRenderMode(renderingMode);

            /// <summary>
            /// The rendering mode for the camera background.
            /// </summary>
            protected abstract XRCameraBackgroundRenderingMode renderingMode { get; }

            /// <summary>
            /// Set up the background render pass.
            /// </summary>
            /// <param name="cameraBackground">The <see cref="ARCameraBackground"/> component that provides the <see cref="Material"/>
            /// and any additional rendering information required by the render pass.</param>
            /// <param name="invertCulling">Whether the culling mode should be inverted.</param>
            public void Setup(ARCameraBackground cameraBackground, bool invertCulling)
            {
                SetupInternal(cameraBackground);

                if (!cameraBackground.TryGetRenderingParameters(out m_CameraBackgroundRenderingParams))
                    m_CameraBackgroundRenderingParams = defaultCameraBackgroundRenderingParams;

                m_BackgroundMaterial = cameraBackground.material;
                m_InvertCulling = invertCulling;
            }

            /// <summary>
            /// Provides inheritors an opportunity to perform any specialized setup during <see cref="ScriptableRenderPass.Setup"/>.
            /// </summary>
            /// <param name="cameraBackground">The <see cref="ARCameraBackground"/> component that provides the <see cref="Material"/>
            /// and any additional rendering information required by the render pass.</param>
            protected virtual void SetupInternal(ARCameraBackground cameraBackground) {}
            
            // Data provided for the static ExecutePass function

            class PassData
            {
                internal CameraData cameraData;
                internal bool invertCulling;
                internal XRCameraBackgroundRenderingParams cameraBackgroundRenderingParams;
                internal Material backgroundMaterial;
            }
            
            /// <summary>
            /// Execute the commands to render the camera background.
            /// This function is used for both RenderGraph and non-RenderGraph paths.
            /// It needs to be static because passing any non-static functions that rely on instance data or on local
            /// variables, would cause the RenderGraph’s RenderFunction lambda to capture those, which will cause GC allocations.
            /// </summary>
            static void ExecutePass(RasterCommandBuffer cmd, PassData passData)
            {
                cmd.BeginSample(k_CustomRenderPassName);

                ARCameraBackground.AddBeforeBackgroundRenderHandler(cmd);

                cmd.SetInvertCulling(passData.invertCulling);

                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);

                cmd.DrawMesh(
                    passData.cameraBackgroundRenderingParams.backgroundGeometry,
                    passData.cameraBackgroundRenderingParams.backgroundTransform,
                    passData.backgroundMaterial);


                cmd.SetViewProjectionMatrices(passData.cameraData.camera.worldToCameraMatrix,
                    passData.cameraData.camera.projectionMatrix);

                cmd.EndSample(k_CustomRenderPassName);
            }

            /// <summary>
            /// Execute the commands to render the camera background.
            /// </summary>
            /// <param name="context">The render context for executing the render commands.</param>
            /// <param name="renderingData">Additional rendering data about the current state of rendering.</param>
            public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
            {
                var cmd = CommandBufferPool.Get(k_CustomRenderPassName);

                m_PassData.cameraData = renderingData.cameraData;
                m_PassData.invertCulling = m_InvertCulling;
                m_PassData.cameraBackgroundRenderingParams = m_CameraBackgroundRenderingParams;
                m_PassData.backgroundMaterial = m_BackgroundMaterial;

                ExecutePass(CommandBufferHelpers.GetRasterCommandBuffer(cmd), m_PassData);
                
                context.ExecuteCommandBuffer(cmd);

                CommandBufferPool.Release(cmd);
            }

            /// <summary>
            /// Clean up any resources for the render pass.
            /// </summary>
            /// <param name="commandBuffer">The command buffer for frame cleanup.</param>
            public override void FrameCleanup(CommandBuffer commandBuffer)
            {
            }
        }

        /// <summary>
        /// The custom render pass to render the camera background before rendering opaques.
        /// </summary>
        class ARCameraBeforeOpaquesRenderPass : ARCameraBackgroundRenderPass
        {
            /// <summary>
            /// Constructs the background render pass.
            /// </summary>
            public ARCameraBeforeOpaquesRenderPass()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            }

            /// <summary>
            /// Configure the render pass by setting the render target and clear values.
            /// </summary>
            /// <param name="commandBuffer">The command buffer for configuration.</param>
            /// <param name="renderTextureDescriptor">The descriptor of the target render texture.</param>
            public override void Configure(CommandBuffer commandBuffer, RenderTextureDescriptor renderTextureDescriptor)
            {
                ConfigureClear(ClearFlag.Depth, Color.clear);
            }

            protected override XRCameraBackgroundRenderingMode renderingMode
                => XRCameraBackgroundRenderingMode.BeforeOpaques;
        }

        /// <summary>
        /// The custom render pass to render the camera background after rendering opaques.
        /// </summary>
        class ARCameraAfterOpaquesRenderPass : ARCameraBackgroundRenderPass
        {
            /// <summary>
            /// Constructs the background render pass.
            /// </summary>
            public ARCameraAfterOpaquesRenderPass()
            {
                renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            }

            /// <summary>
            /// Configure the render pass by setting the render target and clear values.
            /// </summary>
            /// <param name="commandBuffer">The command buffer for configuration.</param>
            /// <param name="renderTextureDescriptor">The descriptor of the target render texture.</param>
            public override void Configure(CommandBuffer commandBuffer, RenderTextureDescriptor renderTextureDescriptor)
            {
                ConfigureClear(ClearFlag.None, Color.clear);
            }

            /// <inheritdoc />
            protected override void SetupInternal(ARCameraBackground cameraBackground)
            {
                if (cameraBackground.GetComponent<AROcclusionManager>()?.enabled ?? false)
                {
                    // If an occlusion texture is being provided, rendering will need
                    // to compare it against the depth texture created by the camera.
                    ConfigureInput(ScriptableRenderPassInput.Depth);
                }
            }

            protected override XRCameraBackgroundRenderingMode renderingMode
                => XRCameraBackgroundRenderingMode.AfterOpaques;
        }
#endif // MODULE_URP_ENABLED
    }
}
