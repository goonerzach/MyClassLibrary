using System;
using System.Collections.Generic;
using System.Linq;
using BoneLib;
using BoneLib.BoneMenu;
using Il2CppSLZ.Marrow;          // Patch 5+ namespace. On older builds use: using SLZ.Rig; / SLZ.Marrow;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(PixelateFaces.Main), "PixelateFaces", "1.0.0", "You")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace PixelateFaces
{
    public class Main : MelonMod
    {
        // ---------- Settings ----------
        private static bool _enabled = true;
        private static int _grid = 8;            // mosaic resolution (NxN "pixels" per cube face). Lower = chunkier.
        private static float _size = 0.22f;      // overlay edge length in metres (before avatar scaling)

        // Name keywords used to classify non-local rigs. Tweak to match your setup.
        private static readonly string[] TimelineKeywords = { "timeline", "clone", "ghost" };
        private static readonly string[] FusionKeywords = { "playerrep", "fusion", "networkplayer" };

        // Offset from the head bone pivot to the face centre (head-local space).
        private static readonly Vector3 FaceOffset = new Vector3(0f, 0.07f, 0.05f);

        private const float ScanInterval = 1.5f; // seconds between scene scans (cheap, not per-frame)

        // ---------- State ----------
        private class Overlay
        {
            public GameObject Go;
            public Transform Head;
        }

        private readonly Dictionary<int, Overlay> _overlays = new Dictionary<int, Overlay>();
        private readonly List<int> _deadKeys = new List<int>();

        private Texture2D _tex;
        private Material _mat;
        private bool _textureDirty = true;
        private bool _scaleDirty = true;
        private bool _enabledDirty = true;
        private float _scanTimer;
        private bool _fusionLoaded;

        // ---------- Lifecycle ----------
        public override void OnInitializeMelon()
        {
            // Soft dependency: never reference LabFusion types directly.
            _fusionLoaded = MelonBase.RegisteredMelons.Any(m =>
                m.Info.Name != null && m.Info.Name.IndexOf("LabFusion", StringComparison.OrdinalIgnoreCase) >= 0);
            LoggerInstance.Msg($"Fusion detected: {_fusionLoaded}");

            BuildMenu();
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            // Old overlays belong to destroyed rigs; drop references.
            _overlays.Clear();
            _scanTimer = 0f;
        }

        public override void OnUpdate()
        {
            // Apply menu changes lazily (slider callbacks fire repeatedly while dragging).
            if (_textureDirty) { RebuildTexture(); _textureDirty = false; }
            if (_scaleDirty) { ApplyScale(); _scaleDirty = false; }
            if (_enabledDirty) { ApplyEnabled(); _enabledDirty = false; }

            if (!_enabled) return;

            _scanTimer -= Time.deltaTime;
            if (_scanTimer <= 0f)
            {
                _scanTimer = ScanInterval;
                Scan();
            }
        }

        // ---------- BoneMenu ----------
        private void BuildMenu()
        {
            var page = Page.Root.CreatePage("Face Pixelator", Color.cyan);

            page.CreateBool("Enabled", Color.white, _enabled, v =>
            {
                _enabled = v;
                _enabledDirty = true;
                _scanTimer = 0f;
            });

            // Pixel grid resolution: lower value = bigger blocks.
            page.CreateInt("Pixel Grid", Color.yellow, 2, _grid, 2, 32, v =>
            {
                _grid = Mathf.Clamp(v, 2, 32);
                _textureDirty = true;
            });

            // Physical size of the censor block.
            page.CreateFloat("Block Size", Color.green, 0.02f, _size, 0.10f, 0.50f, v =>
            {
                _size = Mathf.Clamp(v, 0.10f, 0.50f);
                _scaleDirty = true;
            });
        }

        // ---------- Detection ----------
        private void Scan()
        {
            var local = Player.RigManager;
            var rigs = UnityEngine.Object.FindObjectsOfType<RigManager>();

            foreach (var rm in rigs)
            {
                if (rm == null || rm == local) continue;

                int id = rm.GetInstanceID();
                if (_overlays.TryGetValue(id, out var existing))
                {
                    // Re-attach if the head bone changed (avatar swap).
                    var head = GetHead(rm);
                    if (head != null && head != existing.Head) Attach(existing, head);
                    continue;
                }

                if (!IsTarget(rm)) continue;

                var h = GetHead(rm);
                if (h == null) continue; // avatar not ready yet; retry next scan

                var ov = new Overlay();
                CreateOverlayObject(ov);
                Attach(ov, h);
                _overlays[id] = ov;
            }

            // Clean up overlays whose rigs were destroyed (Timeline clone removed, player left, etc.)
            _deadKeys.Clear();
            foreach (var kv in _overlays)
            {
                if (kv.Value.Go == null) { _deadKeys.Add(kv.Key); continue; }
                if (kv.Value.Head == null)
                {
                    UnityEngine.Object.Destroy(kv.Value.Go);
                    _deadKeys.Add(kv.Key);
                }
            }
            foreach (var k in _deadKeys) _overlays.Remove(k);
        }

        /// <summary>
        /// Every non-local RigManager is either a Fusion player rep or a Timeline clone, so we classify by
        /// name and fall back sensibly. Adjust the keyword arrays if your Timeline/Fusion versions name rigs differently.
        /// </summary>
        private bool IsTarget(RigManager rm)
        {
            string n = (rm.gameObject.name ?? "").ToLowerInvariant();
            string root = (rm.transform.root != null ? rm.transform.root.name : "").ToLowerInvariant();

            bool isTimeline = TimelineKeywords.Any(k => n.Contains(k) || root.Contains(k));
            bool isFusion = _fusionLoaded && FusionKeywords.Any(k => n.Contains(k) || root.Contains(k));

            if (isTimeline || isFusion) return true;

            // Unclassified non-local rig: with Fusion present it's almost certainly a networked player;
            // otherwise it can only be a clone spawned by a mod like Timeline.
            return true;
        }

        private static Transform GetHead(RigManager rm)
        {
            try
            {
                // Preferred: the physics rig's head, which follows the actual tracked/networked head pose.
                var pr = rm.physicsRig;
                if (pr != null && pr.m_head != null) return pr.m_head;
            }
            catch { /* field layout differs by patch; fall through */ }

            try
            {
                // Fallback: humanoid head bone on the current avatar.
                var avatar = rm.avatar;
                if (avatar != null && avatar.animator != null)
                {
                    var bone = avatar.animator.GetBoneTransform(HumanBodyBones.Head);
                    if (bone != null) return bone;
                }
            }
            catch { }

            return null;
        }

        // ---------- Overlay creation ----------
        private void CreateOverlayObject(Overlay ov)
        {
            EnsureMaterial();

            // A cube is visible from any viewing angle and uses a single shared mesh.
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = "PixelateFaces_Overlay";

            var col = go.GetComponent<Collider>();
            if (col != null) UnityEngine.Object.Destroy(col);

            var mr = go.GetComponent<MeshRenderer>();
            mr.sharedMaterial = _mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            go.SetActive(_enabled);
            ov.Go = go;
        }

        private void Attach(Overlay ov, Transform head)
        {
            ov.Head = head;
            var t = ov.Go.transform;
            t.SetParent(head, false);
            t.localPosition = FaceOffset;
            t.localRotation = Quaternion.identity;
            t.localScale = Vector3.one * _size;
        }

        // ---------- Material / texture (shared by every overlay => 1 material, 1 texture) ----------
        private void EnsureMaterial()
        {
            if (_mat != null) return;

            // Shader.Find can return null on Quest if a shader was stripped, so try several unlit options.
            string[] candidates =
            {
                "Universal Render Pipeline/Unlit",
                "Unlit/Texture",
                "Sprites/Default",
                "UI/Default",
                "Standard"
            };

            Shader sh = null;
            foreach (var c in candidates)
            {
                sh = Shader.Find(c);
                if (sh != null) break;
            }

            if (sh == null)
            {
                LoggerInstance.Error("No usable shader found; overlay will be invisible.");
                sh = Shader.Find("Hidden/InternalErrorShader");
            }

            _mat = new Material(sh) { name = "PixelateFaces_Mat", hideFlags = HideFlags.DontUnloadUnusedAsset };

            if (_tex == null) RebuildTexture();
            ApplyTextureToMaterial();
        }

        private void RebuildTexture()
        {
            int n = Mathf.Clamp(_grid, 2, 32);

            if (_tex == null)
            {
                _tex = new Texture2D(n, n, TextureFormat.RGBA32, false)
                {
                    name = "PixelateFaces_Tex",
                    hideFlags = HideFlags.DontUnloadUnusedAsset
                };
            }
            else
            {
                _tex.Reinitialize(n, n);
            }

            _tex.filterMode = FilterMode.Point;   // hard pixel edges = mosaic look
            _tex.wrapMode = TextureWrapMode.Clamp;

            // Deterministic blocky "censor" mosaic (no per-frame shimmer): neutral skin-grey tones with variation.
            var rng = new System.Random(1337);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float v = 0.35f + (float)rng.NextDouble() * 0.45f;
                    float warm = (float)rng.NextDouble() * 0.12f;
                    _tex.SetPixel(x, y, new Color(v + warm, v, v - warm * 0.5f, 1f));
                }
            }
            _tex.Apply(false, false);

            ApplyTextureToMaterial();
        }

        private void ApplyTextureToMaterial()
        {
            if (_mat == null || _tex == null) return;
            _mat.mainTexture = _tex;
            if (_mat.HasProperty("_BaseMap")) _mat.SetTexture("_BaseMap", _tex);
            if (_mat.HasProperty("_MainTex")) _mat.SetTexture("_MainTex", _tex);
        }

        // ---------- Applying menu changes ----------
        private void ApplyScale()
        {
            foreach (var ov in _overlays.Values)
                if (ov.Go != null) ov.Go.transform.localScale = Vector3.one * _size;
        }

        private void ApplyEnabled()
        {
            foreach (var ov in _overlays.Values)
                if (ov.Go != null) ov.Go.SetActive(_enabled);
        }
    }
}
