using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace FpsBase
{
    internal class ItemThumbnailRenderer : IDisposable
    {
        private readonly Camera camera;
        private readonly Light light;
        private readonly RenderPipeline.StandardRequest request = new();
        private readonly MaterialPropertyBlock properties = new();
        private readonly List<Mesh> bakedMeshes = new();

        public ItemThumbnailRenderer()
        {
            var cameraObject = new GameObject("Item Thumbnail Camera", typeof(Camera));
            camera = cameraObject.GetComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.orthographic = true;
            camera.aspect = 1f;
            camera.allowHDR = false;
            camera.useOcclusionCulling = false;

            var lightObject = new GameObject("Thumbnail Light", typeof(Light));
            lightObject.transform.SetParent(camera.transform, false);
            light = lightObject.GetComponent<Light>();
            light.type = LightType.Point;
            light.intensity = 3f;
            light.enabled = false;
        }

        public bool Render(InventoryItem item, RenderTexture target)
        {
            var model = new GameObject("Item Thumbnail Model");
            model.transform.SetPositionAndRotation(new Vector3(0f, -1000f, 0f), Quaternion.Euler(-12f, 72f, 0f));
            model.transform.localScale = item.transform.localScale;

            try
            {
                CopyMeshes(item.transform, model.transform);
                var renderers = model.GetComponentsInChildren<MeshRenderer>();
                if (renderers.Length == 0)
                {
                    return false;
                }

                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }

                float size = Mathf.Max(0.01f, Mathf.Max(bounds.extents.x, bounds.extents.y));
                camera.transform.position = new Vector3(bounds.center.x, bounds.center.y, bounds.min.z - size - 0.05f);
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = bounds.size.z + size + 0.15f;
                camera.orthographicSize = size * 1.15f;
                light.transform.localPosition = new Vector3(-size, size * 1.5f, 0f);
                light.range = (size + bounds.size.z + 0.05f) * 6f;
                light.enabled = true;

                if (GraphicsSettings.currentRenderPipeline == null)
                {
                    camera.targetTexture = target;
                    camera.Render();
                    return true;
                }

                request.destination = target;
                if (!RenderPipeline.SupportsRenderRequest(camera, request))
                {
                    return false;
                }

                RenderPipeline.SubmitRenderRequest(camera, request);
                return true;
            }
            finally
            {
                camera.targetTexture = null;
                request.destination = null;
                light.enabled = false;
                model.SetActive(false);
                Object.Destroy(model);
                foreach (Mesh mesh in bakedMeshes)
                {
                    Object.Destroy(mesh);
                }
                bakedMeshes.Clear();
            }
        }

        public void Dispose()
        {
            if (camera != null)
            {
                Object.Destroy(camera.gameObject);
            }
        }

        private void CopyMeshes(Transform source, Transform target)
        {
            if (source.TryGetComponent(out MeshRenderer meshRenderer) && meshRenderer.enabled &&
                source.TryGetComponent(out MeshFilter filter) && filter.sharedMesh != null)
            {
                AddMesh(target, filter.sharedMesh, meshRenderer);
            }
            else if (source.TryGetComponent(out SkinnedMeshRenderer skinned) && skinned.enabled && skinned.sharedMesh != null)
            {
                var mesh = new Mesh();
                skinned.BakeMesh(mesh);
                bakedMeshes.Add(mesh);
                AddMesh(target, mesh, skinned);
            }

            foreach (Transform child in source)
            {
                if (!child.gameObject.activeSelf)
                {
                    continue;
                }

                var copy = new GameObject(child.name).transform;
                copy.SetParent(target, false);
                copy.SetLocalPositionAndRotation(child.localPosition, child.localRotation);
                copy.localScale = child.localScale;
                CopyMeshes(child, copy);
            }
        }

        private void AddMesh(Transform target, Mesh mesh, Renderer source)
        {
            target.gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = target.gameObject.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = source.sharedMaterials;
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            source.GetPropertyBlock(properties);
            renderer.SetPropertyBlock(properties);
        }
    }
}
