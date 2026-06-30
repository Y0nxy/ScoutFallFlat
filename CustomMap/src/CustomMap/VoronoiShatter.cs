using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;

namespace CustomMap
{
    [RequireComponent(typeof(BoxCollider))]
    [RequireComponent(typeof(MeshRenderer))]
    public class VoronoiShatter : MonoBehaviourPunCallbacks
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        [Header("Shards")]
        public GameObject shardPrefab;
        public PhysicsMaterial physicsMaterial;
        public int shardLayer = 10;
        public float totalMass = 20f;
        public float cellInset = 0f;

        [Header("Impact")]
        public float impactThreshold = 650f;
        public float minExplodeImpulse = 0f;
        public float maxExplodeImpulse = float.PositiveInfinity;
        public float perShardImpulseFraction = 0.3f;
        public float maxShardVelocity = float.PositiveInfinity;

        [Header("Misc")]
        public bool resetOnReload = true;
        public Transform parentObject;

        [Tooltip("Unique root PhotonView ID. Set from your mod loader before Awake.")]
        public int rootViewID = 0;

        // ── Runtime ───────────────────────────────────────────────────────────
        private BoxCollider col;
        private MeshRenderer meshRend;
        private Rigidbody body;
        private Material mat;
        private bool shattered;
        private bool readyForCollision;

        private struct Pending { public GameObject go; public Vector3 force; public Vector3 pos; }
        private readonly List<Pending> pending = new List<Pending>();
        private readonly List<GameObject> cells = new List<GameObject>();
        private readonly Dictionary<int, int> allocatedIDs = new Dictionary<int, int>();

        // ── Lifecycle ─────────────────────────────────────────────────────────
        void Awake()
        {
            col = GetComponent<BoxCollider>();
            meshRend = GetComponent<MeshRenderer>();
            body = GetComponent<Rigidbody>();
            mat = meshRend.sharedMaterial;

            PhotonView pv = GetComponent<PhotonView>() ?? gameObject.AddComponent<PhotonView>();
            if (rootViewID > 0) pv.ViewID = rootViewID;
            else Debug.LogError($"[VoronoiShatter] rootViewID not set on '{name}'!");
        }

        public override void OnEnable()
        {
            base.OnEnable();
            readyForCollision = false;
            StartCoroutine(AllowCollision());
        }

        IEnumerator AllowCollision()
        {
            yield return new WaitForFixedUpdate();
            yield return new WaitForFixedUpdate();
            readyForCollision = true;
        }

        public void SetRootViewID(int id)
        {
            rootViewID = id;
            PhotonView pv = GetComponent<PhotonView>() ?? gameObject.AddComponent<PhotonView>();
            pv.ViewID = id;
        }

        // ── Collision ─────────────────────────────────────────────────────────
        void OnCollisionEnter(Collision c)
        {
            if (!readyForCollision || shattered) return;
            if (c.impulse.magnitude < impactThreshold) return;
            shattered = true;

            Vector3 pt = c.contacts[0].point;
            Vector3 imp = c.impulse;
            int sd = UnityEngine.Random.Range(0, int.MaxValue);

            if (!PhotonNetwork.IsConnected || !PhotonNetwork.InRoom)
            { StartCoroutine(DoShatter(pt, imp, sd)); return; }

            photonView.RPC(nameof(RPC_Shatter), RpcTarget.All,
                pt.x, pt.y, pt.z, imp.x, imp.y, imp.z, sd);
        }

        [PunRPC]
        void RPC_Shatter(float px, float py, float pz,
                         float ix, float iy, float iz, int seed)
        {
            if (shattered && cells.Count > 0) return;
            shattered = true;
            StartCoroutine(DoShatter(new Vector3(px, py, pz), new Vector3(ix, iy, iz), seed));
        }

        // ── Shatter sequence ──────────────────────────────────────────────────
        IEnumerator DoShatter(Vector3 contactWorld, Vector3 impulse, int seed)
        {
            col.enabled = false;
            meshRend.enabled = false;
            if (body != null) body.isKinematic = true;
            yield return new WaitForFixedUpdate();

            Physics.IgnoreLayerCollision(shardLayer, shardLayer, true);

            pending.Clear();
            BuildShards(contactWorld, impulse, seed);

            yield return new WaitForFixedUpdate();
            foreach (var p in pending) if (p.go) p.go.SetActive(true);

            yield return new WaitForFixedUpdate();
            foreach (var p in pending)
            {
                if (!p.go) continue;
                var rb = p.go.GetComponent<Rigidbody>();
                if (rb && !rb.isKinematic)
                    rb.AddForceAtPosition(p.force, p.pos, ForceMode.Impulse);
            }
            pending.Clear();

            yield return new WaitForSeconds(0.25f);
            Physics.IgnoreLayerCollision(shardLayer, shardLayer, false);

            if (PhotonNetwork.IsConnected && PhotonNetwork.InRoom && PhotonNetwork.IsMasterClient)
                StartCoroutine(AllocIDs());
        }

        // ─────────────────────────────────────────────────────────────────────
        // Core: build shards
        // ─────────────────────────────────────────────────────────────────────
        void BuildShards(Vector3 contactWorld, Vector3 impulse, int seed)
        {
            Vector3 sz = col.size;
            Vector3 sc = transform.lossyScale;
            float wx = sz.x * sc.x;
            float wy = sz.y * sc.y;
            float wz = sz.z * sc.z;

            float halfA, halfB, halfT;
            int thinAxis; // 0=X, 1=Y, 2=Z
            if (wx <= wy && wx <= wz) { thinAxis = 0; halfT = sz.x * 0.5f; halfA = sz.y * 0.5f; halfB = sz.z * 0.5f; }
            else if (wy <= wx && wy <= wz) { thinAxis = 1; halfT = sz.y * 0.5f; halfA = sz.x * 0.5f; halfB = sz.z * 0.5f; }
            else { thinAxis = 2; halfT = sz.z * 0.5f; halfA = sz.x * 0.5f; halfB = sz.y * 0.5f; }

            float paneW = halfA * 2f;
            float paneH = halfB * 2f;
            float area = paneW * paneH;
            float massPerArea = totalMass / Mathf.Max(area, 0.0001f);

            Vector3 localContact = transform.InverseTransformPoint(contactWorld) - col.center;
            float cu, cv;
            LocalToAB(localContact, thinAxis, out cu, out cv);
            cu = Mathf.Clamp(cu, -halfA, halfA);
            cv = Mathf.Clamp(cv, -halfB, halfB);

            UnityEngine.Random.State saved = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);

            int total = Mathf.Clamp((int)(area * 18f), 8, 55);
            float[] sa = new float[total];
            float[] sb = new float[total];

            int sc2 = total / 3;
            for (int i = 0; i < sc2; i++)
            {
                sa[i] = UnityEngine.Random.Range(-halfA, halfA);
                sb[i] = UnityEngine.Random.Range(-halfB, halfB);
            }
            float maxR = Mathf.Min(halfA, halfB) * 0.9f;
            for (int i = sc2; i < total; i++)
            {
                bool placed = false;
                for (int t = 0; t < 60; t++)
                {
                    float r = UnityEngine.Random.Range(0f, maxR) * UnityEngine.Random.Range(0f, 1f);
                    Vector2 d = UnityEngine.Random.insideUnitCircle.normalized;
                    float a2 = cu + d.x * r;
                    float b2 = cv + d.y * r;
                    if (a2 >= -halfA && a2 <= halfA && b2 >= -halfB && b2 <= halfB)
                    { sa[i] = a2; sb[i] = b2; placed = true; break; }
                }
                if (!placed)
                {
                    sa[i] = UnityEngine.Random.Range(-halfA, halfA);
                    sb[i] = UnityEngine.Random.Range(-halfB, halfB);
                }
            }

            const float minSeedSepSq = 0.0001f;
            for (int i = 0; i < total; i++)
            {
                for (int j = i + 1; j < total; j++)
                {
                    float dx = sa[i] - sa[j], dy = sb[i] - sb[j];
                    if (dx * dx + dy * dy < minSeedSepSq)
                    {
                        sa[j] = Mathf.Clamp(sa[j] + UnityEngine.Random.Range(-0.05f, 0.05f), -halfA, halfA);
                        sb[j] = Mathf.Clamp(sb[j] + UnityEngine.Random.Range(-0.05f, 0.05f), -halfB, halfB);
                    }
                }
            }

            UnityEngine.Random.state = saved;

            var cellPolys = new List<Vector2>[total];
            for (int k = 0; k < total; k++)
                cellPolys[k] = BuildCellPolygon(k, sa, sb, total, -halfA, halfA, -halfB, halfB);

            Vector3 impDir = impulse.normalized;
            float baseForce = Mathf.Clamp(impulse.magnitude, minExplodeImpulse, maxExplodeImpulse) * perShardImpulseFraction;

            Transform spawnRoot = parentObject != null ? parentObject
                                : transform.parent != null ? transform.parent
                                : transform;

            Vector3 frontNorm = ABTToLocal(0f, 0f, 1f, thinAxis).normalized;
            Vector3 backNorm = ABTToLocal(0f, 0f, -1f, thinAxis).normalized;

            for (int n = 0; n < total; n++)
            {
                var verts2D = cellPolys[n];
                int count = verts2D.Count;
                if (count < 3) continue;

                // 1. Force the 2D polygon to be strictly Counter-Clockwise (CCW)
                float signedArea = 0f;
                for (int q = 0; q < count; q++)
                {
                    Vector2 p0 = verts2D[q];
                    Vector2 p1 = verts2D[(q + 1) % count];
                    signedArea += (p0.x * p1.y - p1.x * p0.y);
                }
                if (signedArea < 0f)
                {
                    verts2D.Reverse();
                }

                if (cellInset > 0f)
                {
                    var inset = new List<Vector2>(count);
                    for (int q = 0; q < count; q++)
                    {
                        Vector2 prev = verts2D[(q - 1 + count) % count];
                        Vector2 curr = verts2D[q];
                        Vector2 next = verts2D[(q + 1) % count];
                        Vector2 d1 = (curr - prev).normalized;
                        Vector2 d2 = (next - curr).normalized;
                        Vector2 bis = (new Vector2(-d1.y, d1.x) + new Vector2(-d2.y, d2.x)).normalized;
                        inset.Add(curr + bis * cellInset);
                    }
                    verts2D = inset; count = verts2D.Count;
                }

                // Centroid
                Vector2 cen = Vector2.zero;
                foreach (var v in verts2D) cen += v;
                cen /= verts2D.Count;

                // 2. Determine if 3D mapping inverted the handedness by checking mapping determinant
                Vector3 m0 = ABTToLocal(verts2D[0].x, verts2D[0].y, 0f, thinAxis);
                Vector3 m1 = ABTToLocal(verts2D[1].x, verts2D[1].y, 0f, thinAxis);
                Vector3 m2 = ABTToLocal(verts2D[2].x, verts2D[2].y, 0f, thinAxis);
                Vector3 mapNorm = Vector3.Cross(m1 - m0, m2 - m0);
                bool flip3D = Vector3.Dot(mapNorm, frontNorm) < 0f;

                int totalVerts = count * 2 + count * 4;
                var mv = new Vector3[totalVerts];
                var mn = new Vector3[totalVerts];
                var muv = new Vector2[totalVerts];
                var tris = new List<int>();

                float uvScaleA = 1f / (halfA * 2f);
                float uvScaleB = 1f / (halfB * 2f);

                // ── Front face ──
                for (int q = 0; q < count; q++)
                {
                    Vector2 rel = verts2D[q] - cen;
                    mv[q] = ABTToLocal(rel.x, rel.y, halfT, thinAxis);
                    mn[q] = frontNorm;
                    muv[q] = new Vector2((verts2D[q].x + halfA) * uvScaleA, (verts2D[q].y + halfB) * uvScaleB);
                }
                for (int q = 1; q < count - 1; q++)
                {
                    if (!flip3D) { tris.Add(0); tris.Add(q); tris.Add(q + 1); }
                    else { tris.Add(0); tris.Add(q + 1); tris.Add(q); }
                }

                // ── Back face ──
                int bOff = count;
                for (int q = 0; q < count; q++)
                {
                    Vector2 rel = verts2D[q] - cen;
                    mv[bOff + q] = ABTToLocal(rel.x, rel.y, -halfT, thinAxis);
                    mn[bOff + q] = backNorm;
                    muv[bOff + q] = new Vector2((verts2D[q].x + halfA) * uvScaleA, (verts2D[q].y + halfB) * uvScaleB);
                }
                for (int q = 1; q < count - 1; q++)
                {
                    if (!flip3D) { tris.Add(bOff); tris.Add(bOff + q + 1); tris.Add(bOff + q); }
                    else { tris.Add(bOff); tris.Add(bOff + q); tris.Add(bOff + q + 1); }
                }

                // ── Side walls ──
                int sOff = count * 2;
                for (int q = 0; q < count; q++)
                {
                    int next = (q + 1) % count;
                    Vector2 rA = verts2D[q] - cen;
                    Vector2 rB = verts2D[next] - cen;

                    Vector3 vFrontA = ABTToLocal(rA.x, rA.y, halfT, thinAxis);
                    Vector3 vFrontB = ABTToLocal(rB.x, rB.y, halfT, thinAxis);
                    Vector3 vBackA = ABTToLocal(rA.x, rA.y, -halfT, thinAxis);
                    Vector3 vBackB = ABTToLocal(rB.x, rB.y, -halfT, thinAxis);

                    Vector2 edgeDir = (rB - rA).normalized;
                    Vector2 outDir2D = new Vector2(edgeDir.y, -edgeDir.x);
                    Vector3 sideNorm = ABTToLocal(outDir2D.x, outDir2D.y, 0f, thinAxis).normalized;

                    int vi = sOff + q * 4;
                    mv[vi + 0] = vFrontA; mn[vi + 0] = sideNorm;
                    mv[vi + 1] = vFrontB; mn[vi + 1] = sideNorm;
                    mv[vi + 2] = vBackB; mn[vi + 2] = sideNorm;
                    mv[vi + 3] = vBackA; mn[vi + 3] = sideNorm;

                    float edgeLen = (rB - rA).magnitude;
                    muv[vi + 0] = new Vector2(0f, 1f);
                    muv[vi + 1] = new Vector2(edgeLen, 1f);
                    muv[vi + 2] = new Vector2(edgeLen, 0f);
                    muv[vi + 3] = new Vector2(0f, 0f);

                    // 3. Conditional quad winding for sides
                    if (!flip3D)
                    {
                        tris.Add(vi + 0); tris.Add(vi + 3); tris.Add(vi + 2);
                        tris.Add(vi + 0); tris.Add(vi + 2); tris.Add(vi + 1);
                    }
                    else
                    {
                        tris.Add(vi + 0); tris.Add(vi + 1); tris.Add(vi + 2);
                        tris.Add(vi + 0); tris.Add(vi + 2); tris.Add(vi + 3);
                    }
                }

                var mesh = new Mesh { name = "shard" + n };
                mesh.vertices = mv;
                mesh.normals = mn;
                mesh.uv = muv;
                mesh.triangles = tris.ToArray();
                mesh.RecalculateBounds();

                float polyArea = Mathf.Abs(signedArea) * 0.5f;

                // ── Create shard GO ──
                GameObject shard;
                MeshFilter mf = null;
                MeshRenderer mr = null;
                AudioSource sfx = null;

                if (shardPrefab == null)
                {
                    shard = new GameObject("shard" + n) { layer = shardLayer };
                }
                else
                {
                    shard = Instantiate(shardPrefab); shard.name = "shard" + n;
                    mf = shard.GetComponent<MeshFilter>();
                    mr = shard.GetComponent<MeshRenderer>();
                    sfx = shard.GetComponent<AudioSource>();
                    var ec = shard.GetComponent<Collider>();
                    if (ec) Destroy(ec);
                }

                shard.SetActive(false);
                shard.layer = LayerMask.NameToLayer("Map");

                if (!mf) mf = shard.AddComponent<MeshFilter>();
                mf.mesh = mesh;
                if (!mr) mr = shard.AddComponent<MeshRenderer>();
                shard.AddComponent<RigidBodyStandable>();
                if (mat) mr.sharedMaterial = mat;

                Vector3 cenLocal = ABTToLocal(cen.x, cen.y, 0f, thinAxis) + col.center;
                Vector3 cenWorld = transform.TransformPoint(cenLocal);

                shard.transform.SetPositionAndRotation(cenWorld, transform.rotation);
                shard.transform.SetParent(spawnRoot, worldPositionStays: true);

                var bc = shard.AddComponent<MeshCollider>();
                bc.sharedMaterial = physicsMaterial;
                bc.convex = true;
                //bc.center = Vector3.zero;
                //bc.size = mesh.bounds.size + Vector3.one * 0.001f;

                var rb = shard.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(0.05f, polyArea * massPerArea);

                if (!sfx) sfx = shard.AddComponent<AudioSource>();
                sfx.spatialBlend = 1f; sfx.playOnAwake = false;
                sfx.pitch = Mathf.Clamp(8f / rb.mass, 0.85f, 1.15f);

                float fmag = Mathf.Clamp(baseForce * 0.005f, 0f, maxShardVelocity * rb.mass);
                Vector3 seedL = ABTToLocal(sa[n], sb[n], 0f, thinAxis) + col.center;
                Vector3 seedW = transform.TransformPoint(seedL);
                Vector3 fpos = Vector3.Lerp(contactWorld, seedW, 0.3f);

                pending.Add(new Pending { go = shard, force = -impDir * fmag, pos = fpos });
                cells.Add(shard);
            }
        }

        // ── Coordinate helpers ────────────────────────────────────────────────
        static Vector3 ABTToLocal(float a, float b, float t, int thinAxis)
        {
            switch (thinAxis)
            {
                case 0: return new Vector3(t, a, b);
                case 1: return new Vector3(a, t, b);
                default: return new Vector3(a, b, t);
            }
        }

        static void LocalToAB(Vector3 local, int thinAxis, out float a, out float b)
        {
            switch (thinAxis)
            {
                case 0: a = local.y; b = local.z; break;
                case 1: a = local.x; b = local.z; break;
                default: a = local.x; b = local.y; break;
            }
        }

        // ── Voronoi cells via half-plane clipping ──────────────────────────────
        static List<Vector2> BuildCellPolygon(int i, float[] sx, float[] sy, int n,
            float minA, float maxA, float minB, float maxB)
        {
            var poly = new List<Vector2>
            {
                new Vector2(minA, minB),
                new Vector2(maxA, minB),
                new Vector2(maxA, maxB),
                new Vector2(minA, maxB)
            };

            float px = sx[i], py = sy[i];

            for (int j = 0; j < n; j++)
            {
                if (j == i) continue;
                float qx = sx[j], qy = sy[j];

                float mx = (px + qx) * 0.5f, my = (py + qy) * 0.5f;
                float dx = qx - px, dy = qy - py;
                float distSq = dx * dx + dy * dy;
                if (distSq < 1e-10f) continue;

                poly = ClipPolygonHalfPlane(poly, mx, my, dx, dy);
                if (poly.Count == 0) break;
            }
            return poly;
        }

        static List<Vector2> ClipPolygonHalfPlane(List<Vector2> poly, float mx, float my, float dirX, float dirY)
        {
            var out_ = new List<Vector2>();
            int count = poly.Count;
            if (count == 0) return out_;

            for (int k = 0; k < count; k++)
            {
                Vector2 curr = poly[k];
                Vector2 next = poly[(k + 1) % count];

                float dCurr = (curr.x - mx) * dirX + (curr.y - my) * dirY;
                float dNext = (next.x - mx) * dirX + (next.y - my) * dirY;

                bool currIn = dCurr <= 0f;
                bool nextIn = dNext <= 0f;

                if (currIn) out_.Add(curr);

                if (currIn != nextIn)
                {
                    float t = dCurr / (dCurr - dNext);
                    Vector2 inter = curr + (next - curr) * t;
                    out_.Add(inter);
                }
            }
            return out_;
        }

        // ── Photon ViewIDs ────────────────────────────────────────────────────
        IEnumerator AllocIDs()
        {
            yield return null;
            allocatedIDs.Clear();
            for (int i = 0; i < cells.Count; i++)
                allocatedIDs[i] = PhotonNetwork.AllocateViewID(true);
            ApplyIDs(allocatedIDs);
            int[] idx = new int[allocatedIDs.Count], ids = new int[allocatedIDs.Count];
            int k = 0;
            foreach (var kv in allocatedIDs) { idx[k] = kv.Key; ids[k] = kv.Value; k++; }
            photonView.RPC(nameof(RPC_ReceiveIDs), RpcTarget.Others, idx, ids);
        }

        [PunRPC]
        void RPC_ReceiveIDs(int[] idx, int[] ids)
        {
            var m = new Dictionary<int, int>();
            for (int i = 0; i < idx.Length; i++) m[idx[i]] = ids[i];
            ApplyIDs(m);
        }

        [PunRPC]
        void RPC_RequestIDs(int actor)
        {
            if (!PhotonNetwork.IsMasterClient || allocatedIDs.Count == 0) return;
            int[] idx = new int[allocatedIDs.Count], ids = new int[allocatedIDs.Count];
            int k = 0; foreach (var kv in allocatedIDs) { idx[k] = kv.Key; ids[k] = kv.Value; k++; }
            var pl = PhotonNetwork.CurrentRoom.GetPlayer(actor);
            if (pl != null) photonView.RPC(nameof(RPC_ReceiveIDs), pl, idx, ids);
        }

        void ApplyIDs(Dictionary<int, int> map)
        {
            bool master = PhotonNetwork.IsMasterClient;
            foreach (var kv in map)
            {
                if (kv.Key >= cells.Count) continue;
                var go = cells[kv.Key];
                var pv = go.GetComponent<PhotonView>() ?? go.AddComponent<PhotonView>();
                var tv = go.GetComponent<PhotonTransformView>() ?? go.AddComponent<PhotonTransformView>();
                var rv = go.GetComponent<PhotonRigidbodyView>() ?? go.AddComponent<PhotonRigidbodyView>();
                tv.m_SynchronizePosition = true; tv.m_SynchronizeRotation = true; tv.m_SynchronizeScale = false;
                rv.m_SynchronizeVelocity = true; rv.m_SynchronizeAngularVelocity = true;
                pv.ObservedComponents = new List<Component> { tv, rv };
                pv.Synchronization = ViewSynchronization.UnreliableOnChange;
                pv.OwnershipTransfer = OwnershipOption.Fixed;
                pv.ViewID = kv.Value;
                var rb = go.GetComponent<Rigidbody>();
                if (rb) rb.isKinematic = !master;
            }
        }

        public override void OnMasterClientSwitched(Photon.Realtime.Player p)
        {
            if (!p.IsLocal) return;
            allocatedIDs.Clear();
            for (int i = 0; i < cells.Count; i++)
            {
                var pv = cells[i].GetComponent<PhotonView>();
                if (pv) allocatedIDs[i] = pv.ViewID;
            }
            foreach (var c in cells)
            {
                var rb = c.GetComponent<Rigidbody>();
                if (rb) rb.isKinematic = false;
            }
        }

        // ── Reset ─────────────────────────────────────────────────────────────
        public void ResetState()
        {
            if (!resetOnReload) return;
            foreach (var c in cells) if (c) Destroy(c);
            cells.Clear(); pending.Clear(); allocatedIDs.Clear();
            shattered = false;
            col.enabled = true; meshRend.enabled = true;
            if (body) body.isKinematic = false;
            readyForCollision = false;
            StartCoroutine(AllowCollision());
        }
    }
}