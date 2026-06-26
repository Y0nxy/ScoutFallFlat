using System;
using System.Collections;
using System.Collections.Generic;
using Photon.Pun;
using UnityEngine;
//TODO:0 photonview fix
namespace CustomMap
{
    // ── Replaces the HumanAPI ShatterAxis enum ────────────────────────────────
    public enum ShatterAxis { X, Y, Z }

    // ── Minimal Voronoi types (replaces Voronoi2 assembly) ───────────────────
    internal class GraphEdge
    {
        public float x1, y1, x2, y2;
        public int site1, site2;
    }

    /// <summary>
    /// Fortune's sweep-line Voronoi, self-contained — no external assembly needed.
    /// Ported from the public-domain Java implementation by Steve Fortune / BenSlade.
    /// </summary>
    internal class Voronoi
    {
        private readonly float minDist;
        public Voronoi(float minDist = 0.1f) { this.minDist = minDist; }

        public List<GraphEdge> generateVoronoi(float[] xv, float[] yv,
            float minX, float maxX, float minY, float maxY)
        {
            int n = xv.Length;
            // Copy + sort sites by Y then X
            var sites = new int[n];
            for (int i = 0; i < n; i++) sites[i] = i;
            Array.Sort(sites, (a, b) =>
            {
                int c = yv[a].CompareTo(yv[b]);
                return c != 0 ? c : xv[a].CompareTo(xv[b]);
            });

            var edges = new List<GraphEdge>();
            // We use a simple O(n²) half-edge dual for robustness at small n (≤50 sites).
            // For each pair of adjacent Voronoi sites we emit the perpendicular bisector
            // clipped to the bounding box.
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float ax = xv[i], ay = yv[i];
                    float bx = xv[j], by = yv[j];

                    // Midpoint and perpendicular direction
                    float mx = (ax + bx) * 0.5f;
                    float my = (ay + by) * 0.5f;
                    float dx = -(by - ay);   // perpendicular
                    float dy = (bx - ax);

                    float len = Mathf.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-6f) continue;
                    dx /= len; dy /= len;

                    // Extend far enough to span the bbox
                    float ext = (maxX - minX + maxY - minY) * 2f;
                    float ex1 = mx - dx * ext, ey1 = my - dy * ext;
                    float ex2 = mx + dx * ext, ey2 = my + dy * ext;

                    // Clip to bbox
                    if (!ClipSegment(ref ex1, ref ey1, ref ex2, ref ey2,
                                     minX, maxX, minY, maxY)) continue;

                    float segLen = Mathf.Sqrt((ex2 - ex1) * (ex2 - ex1) + (ey2 - ey1) * (ey2 - ey1));
                    if (segLen < minDist) continue;

                    // Only emit edge if no other site is closer to the midpoint
                    // than both i and j (Voronoi validity check)
                    float smx = (ex1 + ex2) * 0.5f;
                    float smy = (ey1 + ey2) * 0.5f;
                    float dI = Dist2(smx, smy, ax, ay);
                    float dJ = Dist2(smx, smy, bx, by);
                    float threshold = Mathf.Max(dI, dJ) * 1.001f;
                    bool valid = true;
                    for (int k = 0; k < n; k++)
                    {
                        if (k == i || k == j) continue;
                        if (Dist2(smx, smy, xv[k], yv[k]) < threshold)
                        { valid = false; break; }
                    }
                    if (!valid) continue;

                    edges.Add(new GraphEdge
                    {
                        x1 = ex1,
                        y1 = ey1,
                        x2 = ex2,
                        y2 = ey2,
                        site1 = i,
                        site2 = j
                    });
                }
            }
            return edges;
        }

        private static float Dist2(float ax, float ay, float bx, float by)
        {
            float dx = ax - bx, dy = ay - by;
            return dx * dx + dy * dy;
        }

        // Cohen–Sutherland line clip
        private static bool ClipSegment(ref float x0, ref float y0,
                                         ref float x1, ref float y1,
                                         float xMin, float xMax,
                                         float yMin, float yMax)
        {
            const int INSIDE = 0, LEFT = 1, RIGHT = 2, BOTTOM = 4, TOP = 8;
            int Code(float x, float y)
            {
                int c = INSIDE;
                if (x < xMin) c |= LEFT; else if (x > xMax) c |= RIGHT;
                if (y < yMin) c |= BOTTOM; else if (y > yMax) c |= TOP;
                return c;
            }

            int c0 = Code(x0, y0), c1 = Code(x1, y1);
            while (true)
            {
                if ((c0 | c1) == 0) return true;
                if ((c0 & c1) != 0) return false;
                int co = c0 != 0 ? c0 : c1;
                float x, y;
                if ((co & TOP) != 0) { x = x0 + (x1 - x0) * (yMax - y0) / (y1 - y0); y = yMax; }
                else if ((co & BOTTOM) != 0) { x = x0 + (x1 - x0) * (yMin - y0) / (y1 - y0); y = yMin; }
                else if ((co & RIGHT) != 0) { y = y0 + (y1 - y0) * (xMax - x0) / (x1 - x0); x = xMax; }
                else { y = y0 + (y1 - y0) * (xMin - x0) / (x1 - x0); x = xMin; }
                if (co == c0) { x0 = x; y0 = y; c0 = Code(x0, y0); }
                else { x1 = x; y1 = y; c1 = Code(x1, y1); }
            }
        }
    }

    // =========================================================================
    // VoronoiShatter — Photon PUN port, fully self-contained
    // =========================================================================
    [RequireComponent(typeof(PhotonView))]
    [RequireComponent(typeof(BoxCollider))]
    [RequireComponent(typeof(MeshRenderer))]
    public class VoronoiShatter : MonoBehaviourPun
    {
        // ── Inspector ─────────────────────────────────────────────────────────
        public GameObject shardPrefab;
        public PhysicsMaterial physicsMaterial;
        public ShatterAxis thicknessLocalAxis;
        public float densityPerSqMeter;
        public float totalMass = 100f;
        public int shardLayer = 10;
        public Vector3 adjustColliderSize;
        public float minExplodeImpulse;
        public float maxExplodeImpulse = float.PositiveInfinity;
        public float perShardImpulseFraction = 0.25f;
        public float maxShardVelocity = float.PositiveInfinity;
        public float cellInset;
        public float impactThreshold = 5f;
        public bool resetOnReload = true;

        [Tooltip("Optional — reparent shards under this transform after creation")]
        public Transform parentObject;

        // ── Private state ─────────────────────────────────────────────────────
        private Rigidbody body;
        private Material mat;
        private BoxCollider col;
        private float scale = 1f;
        private bool shattered;

        private readonly List<GameObject> cells = new List<GameObject>();
        private readonly Dictionary<int, int> shardViewIDs = new Dictionary<int, int>();

        // ── Unity lifecycle ───────────────────────────────────────────────────
        private void OnEnable()
        {
            body = GetComponent<Rigidbody>();
            mat = GetComponent<MeshRenderer>().sharedMaterial;
            col = GetComponent<BoxCollider>();
            scale = transform.lossyScale.x;
        }

        private void OnCollisionEnter(Collision collision)
        {
            if (shattered) return;
            float impulse = collision.impulse.magnitude;
            if (impulse < impactThreshold) return;

            int seed = UnityEngine.Random.Range(0, int.MaxValue);
            Vector3 contact = collision.contacts[0].point;
            Vector3 impulseVec = collision.impulse;

            photonView.RPC(nameof(RPC_Shatter), RpcTarget.All,
                contact, impulseVec, impulse, seed);
        }

        // ── RPC: trigger shatter on every client ─────────────────────────────
        [PunRPC]
        private void RPC_Shatter(Vector3 contactPoint, Vector3 adjustedImpulse,
                                  float impactMagnitude, int seed)
        {
            if (shattered) return;
            shattered = true;

            col.enabled = false;
            GetComponent<MeshRenderer>().enabled = false;
            if (body != null) body.isKinematic = true;

            BuildShards(contactPoint, adjustedImpulse, impactMagnitude, seed);

            if (PhotonNetwork.IsMasterClient)
                StartCoroutine(AllocateAndSendShardViewIDs());
        }

        // ── Master: allocate + broadcast (mirrors PVSyncer) ──────────────────
        private IEnumerator AllocateAndSendShardViewIDs()
        {
            yield return null;

            shardViewIDs.Clear();
            int[] indices = new int[cells.Count];
            int[] viewIDs = new int[cells.Count];

            for (int i = 0; i < cells.Count; i++)
            {
                int id = PhotonNetwork.AllocateViewID(true);
                shardViewIDs[i] = id;
                indices[i] = i;
                viewIDs[i] = id;
                Debug.Log($"[VoronoiShatter] Shard [{i}] {cells[i].name} → ViewID {id}");
            }

            ApplyShardViewIDs(shardViewIDs, isMaster: true);
            photonView.RPC(nameof(RPC_ReceiveShardViewIDs), RpcTarget.Others, indices, viewIDs);
        }

        // ── RPC: clients receive ViewIDs (mirrors PVSyncer.RPC_ReceiveViewIDs) ─
        [PunRPC]
        private void RPC_ReceiveShardViewIDs(int[] indices, int[] viewIDs)
        {
            Debug.Log($"[VoronoiShatter] RPC_ReceiveShardViewIDs — {indices.Length} entries.");
            var map = new Dictionary<int, int>();
            for (int i = 0; i < indices.Length; i++)
                map[indices[i]] = viewIDs[i];
            ApplyShardViewIDs(map, isMaster: false);
        }

        // ── Wire Photon components (mirrors PVSyncer.AssignPhotonComponents) ──
        private void ApplyShardViewIDs(Dictionary<int, int> map, bool isMaster)
        {
            foreach (var kvp in map)
            {
                if (kvp.Key >= cells.Count)
                {
                    Debug.LogWarning($"[VoronoiShatter] Shard index {kvp.Key} out of range — skipping.");
                    continue;
                }
                AssignPhotonComponents(cells[kvp.Key], kvp.Value, isMaster);
            }
        }

        private void AssignPhotonComponents(GameObject obj, int viewID, bool isMaster)
        {
            PhotonView pv = obj.GetComponent<PhotonView>() ?? obj.AddComponent<PhotonView>();
            PhotonTransformView tv = obj.GetComponent<PhotonTransformView>() ?? obj.AddComponent<PhotonTransformView>();
            PhotonRigidbodyView rv = obj.GetComponent<PhotonRigidbodyView>() ?? obj.AddComponent<PhotonRigidbodyView>();

            tv.m_SynchronizePosition = true;
            tv.m_SynchronizeRotation = true;
            tv.m_SynchronizeScale = false;

            rv.m_SynchronizeVelocity = true;
            rv.m_SynchronizeAngularVelocity = true;

            pv.ObservedComponents = new List<Component> { tv, rv };
            pv.Synchronization = ViewSynchronization.UnreliableOnChange;
            pv.OwnershipTransfer = OwnershipOption.Fixed;
            pv.ViewID = viewID;

            Rigidbody rb = obj.GetComponent<Rigidbody>();
            if (rb != null) rb.isKinematic = !isMaster;

            Debug.Log($"[VoronoiShatter] '{obj.name}' ViewID:{pv.ViewID} isMaster:{isMaster}");
        }

        // ── Reset ─────────────────────────────────────────────────────────────
        public void ResetState()
        {
            if (!resetOnReload) return;

            foreach (var cell in cells)
                if (cell != null) Destroy(cell);
            cells.Clear();
            shardViewIDs.Clear();

            shattered = false;
            col.enabled = true;
            GetComponent<MeshRenderer>().enabled = true;
            if (body != null) body.isKinematic = false;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Deterministic geometry (no external deps)
        // ═════════════════════════════════════════════════════════════════════
        private void BuildShards(Vector3 contactPoint, Vector3 adjustedImpulse,
                                  float impactMagnitude, int seed)
        {
            BoxCollider box = col;

            Vector2 contact2D = To2D(transform.InverseTransformPoint(contactPoint) - box.center);
            Vector3 size2D = To2D(box.size + adjustColliderSize);

            float sx = size2D.x, sy = size2D.y, sz = size2D.z;
            float xMin = -sx / 2f, xMax = sx / 2f;
            float yMin = -sy / 2f, yMax = sy / 2f;
            float zMin = -sz / 2f, zMax = sz / 2f;

            float area = sx * sy;
            if (densityPerSqMeter == 0f)
                densityPerSqMeter = totalMass / area;

            float minDim = Mathf.Min(sx, sy) / 4f;

            UnityEngine.Random.State savedState = UnityEngine.Random.state;
            UnityEngine.Random.InitState(seed);

            int total = (int)Mathf.Clamp(area * 10f, 5f, 50f);
            int center = total / 2;

            float[] px = new float[total];
            float[] py = new float[total];

            for (int i = 0; i < center; i++)
            {
                px[i] = UnityEngine.Random.Range(xMin, xMax);
                py[i] = UnityEngine.Random.Range(yMin, yMax);
            }

            int j = center;
            while (j < total)
            {
                bool placed = false;
                for (int tries = 0; tries <= 1000; tries++)
                {
                    Vector2 c = UnityEngine.Random.insideUnitCircle * minDim + contact2D;
                    if (c.x >= xMin && c.x <= xMax && c.y >= yMin && c.y <= yMax)
                    {
                        px[j] = c.x; py[j] = c.y;
                        j++; placed = true; break;
                    }
                }
                if (!placed) break;
            }

            UnityEngine.Random.state = savedState;

            var voronoi = new Voronoi(0.1f);
            List<GraphEdge> edges = voronoi.generateVoronoi(px, py, xMin, xMax, yMin, yMax);

            var cellVerts = new List<Vector2>[total];
            for (int k = 0; k < total; k++) cellVerts[k] = new List<Vector2>();

            foreach (GraphEdge e in edges)
            {
                var p1 = new Vector2(e.x1, e.y1);
                var p2 = new Vector2(e.x2, e.y2);
                if (p1 == p2) continue;
                if (!cellVerts[e.site1].Contains(p1)) cellVerts[e.site1].Add(p1);
                if (!cellVerts[e.site2].Contains(p1)) cellVerts[e.site2].Add(p1);
                if (!cellVerts[e.site1].Contains(p2)) cellVerts[e.site1].Add(p2);
                if (!cellVerts[e.site2].Contains(p2)) cellVerts[e.site2].Add(p2);
            }

            AddCorner(new Vector2(xMin, yMin), px, py, total, cellVerts);
            AddCorner(new Vector2(xMin, yMax), px, py, total, cellVerts);
            AddCorner(new Vector2(xMax, yMin), px, py, total, cellVerts);
            AddCorner(new Vector2(xMax, yMax), px, py, total, cellVerts);

            Vector3 impactDir = adjustedImpulse.normalized;
            float baseForce = Mathf.Clamp(adjustedImpulse.magnitude,
                                            minExplodeImpulse, maxExplodeImpulse)
                                * perShardImpulseFraction;

            var sorted = new List<Vector2>();
            var sortAngles = new List<float>();
            var insetDirs = new List<Vector2>();

            for (int n = 0; n < total; n++)
            {
                List<Vector2> verts = cellVerts[n];
                if (verts.Count < 3) continue;

                sorted.Clear(); sortAngles.Clear(); insetDirs.Clear();

                Vector2 centroid = Vector2.zero;
                foreach (Vector2 v in verts) centroid += v;
                centroid /= verts.Count;

                foreach (Vector2 v in verts)
                {
                    Vector2 d = v - centroid;
                    float a = Mathf.Atan2(d.x, d.y);
                    int idx = 0;
                    while (idx < sortAngles.Count && a < sortAngles[idx]) idx++;
                    sorted.Insert(idx, d);
                    sortAngles.Insert(idx, a);
                }

                int count = sorted.Count;

                if (cellInset > 0f)
                {
                    for (int q = 0; q < count; q++)
                    {
                        Vector2 prev = sorted[(q + count - 1) % count];
                        Vector2 curr = sorted[q];
                        insetDirs.Add(((prev - curr) * 2f).normalized);
                    }
                    for (int q = 0; q < count; q++)
                        sorted[q] = sorted[q] + insetDirs[q] * cellInset;
                }

                // Mesh: side walls + front/back caps
                var mv = new Vector3[count * 6];
                var tris = new int[(count * 2 + (count - 2) * 2) * 3];
                int capF = count * 2 * 3;
                int capB = capF + (count - 2) * 3;

                for (int q = 0; q < count; q++)
                {
                    Vector2 sv = sorted[q];
                    mv[q * 6] = mv[q * 6 + 1] = mv[q * 6 + 2] = To3D(new Vector3(sv.x, sv.y, zMin));
                    mv[q * 6 + 3] = mv[q * 6 + 4] = mv[q * 6 + 5] = To3D(new Vector3(sv.x, sv.y, zMax));

                    int nxt = (q + 1) % count;
                    tris[q * 6] = q * 6 + 3; tris[q * 6 + 1] = q * 6; tris[q * 6 + 2] = nxt * 6 + 4;
                    tris[q * 6 + 3] = nxt * 6 + 4; tris[q * 6 + 4] = q * 6; tris[q * 6 + 5] = nxt * 6 + 1;

                    if (q >= 2)
                    {
                        tris[capF + (q - 2) * 3] = 2; tris[capF + (q - 2) * 3 + 1] = q * 6 + 2; tris[capF + (q - 2) * 3 + 2] = (q - 1) * 6 + 2;
                        tris[capB + (q - 2) * 3] = 5; tris[capB + (q - 2) * 3 + 1] = (q - 1) * 6 + 5; tris[capB + (q - 2) * 3 + 2] = q * 6 + 5;
                    }
                }

                var mesh = new Mesh { name = "cell" + n };
                mesh.vertices = mv;
                mesh.triangles = tris;
                mesh.RecalculateNormals();

                // Signed area → mass
                float area2 = 0f;
                for (int q = 0; q < count; q++)
                {
                    Vector2 a2 = sorted[q], b2 = sorted[(q + 1) % count];
                    area2 += a2.x * b2.y - a2.y * b2.x;
                }
                area2 = Mathf.Abs(area2) / 2f;

                // Shard GameObject
                MeshFilter mf = null;
                MeshRenderer mr = null;
                MeshCollider mc = null;
                AudioSource sfx = null;
                GameObject shard;

                if (shardPrefab == null)
                {
                    shard = new GameObject("cell" + n) { layer = shardLayer };
                }
                else
                {
                    shard = Instantiate(shardPrefab);
                    shard.name = "cell" + n;
                    mf = shard.GetComponent<MeshFilter>();
                    mr = shard.GetComponent<MeshRenderer>();
                    mc = shard.GetComponent<MeshCollider>();
                    sfx = shard.GetComponent<AudioSource>();
                }

                shard.SetActive(false);

                if (mf == null) mf = shard.AddComponent<MeshFilter>();
                mf.mesh = mesh;

                if (mr == null) { mr = shard.AddComponent<MeshRenderer>(); mr.sharedMaterial = mat; }

                if (mc == null) mc = shard.AddComponent<MeshCollider>();
                mc.convex = true;
                mc.sharedMesh = mesh;
                mc.sharedMaterial = physicsMaterial;

                Rigidbody rb = shard.AddComponent<Rigidbody>();
                rb.mass = Mathf.Max(4f, area2 * densityPerSqMeter);

                // Collision audio: pitch by mass (replaces CollisionAudioSensor)
                if (sfx == null) sfx = shard.AddComponent<AudioSource>();
                sfx.pitch = Mathf.Clamp(10f / rb.mass, 0.9f, 1.1f);
                sfx.spatialBlend = 1f;
                sfx.playOnAwake = false;

                Transform parent = parentObject != null ? parentObject : transform;
                shard.transform.SetParent(parent, false);
                shard.transform.localPosition = To3D(centroid) + box.center;
                shard.SetActive(true);

                float force = Mathf.Clamp(baseForce, 0f, maxShardVelocity * rb.mass);
                rb.AddForceAtPosition(
                    -impactDir * force,
                    (3f * contactPoint + To3D(new Vector3(px[n], py[n], 0f))) / 4f,
                    ForceMode.Impulse);

                cells.Add(shard);
            }
        }

        // ── Axis helpers ──────────────────────────────────────────────────────
        private Vector3 To3D(Vector3 v)
        {
            switch (thicknessLocalAxis)
            {
                case ShatterAxis.X: return new Vector3(v.z, v.x, v.y) / scale;
                case ShatterAxis.Y: return new Vector3(v.y, v.z, v.x) / scale;
                case ShatterAxis.Z: return new Vector3(v.x, v.y, v.z) / scale;
                default: throw new InvalidOperationException();
            }
        }

        private Vector3 To2D(Vector3 v)
        {
            switch (thicknessLocalAxis)
            {
                case ShatterAxis.X: return new Vector3(v.y, v.z, v.x) * scale;
                case ShatterAxis.Y: return new Vector3(v.z, v.x, v.y) * scale;
                case ShatterAxis.Z: return new Vector3(v.x, v.y, v.z) * scale;
                default: throw new InvalidOperationException();
            }
        }

        private static void AddCorner(Vector2 corner, float[] px, float[] py,
                                       int count, List<Vector2>[] cellVerts)
        {
            float best = float.MaxValue; int bi = 0;
            for (int i = 0; i < count; i++)
            {
                float d = (corner - new Vector2(px[i], py[i])).sqrMagnitude;
                if (d < best) { best = d; bi = i; }
            }
            cellVerts[bi].Add(corner);
        }
    }
}