using System.Collections;
using UnityEngine;
using UnityEngine.UI;

namespace UIBatch
{
    public class UIBatchTest : MonoBehaviour
    {
        public Graphic canvasElement;
        public Graphic canvasElement2;

        private Mesh mesh;
        private Mesh mesh2;

        public bool drawGizmos = false;

        [ContextMenu("计算相交检测")]
        public void CalOverLerps()
        {
            Debug.Log(UIBatchTool.Overlaps(canvasElement, canvasElement2));
        }

        [ContextMenu("测试合批")]
        public void TraverseAllUINodes()
        {
            new UIBatch().TraverseAllUINodes(transform);
        }

        private void OnDrawGizmos()
        {
            if (!drawGizmos)
                return;
            if (mesh != null)
            {
                var vertices = mesh.vertices;
                var triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 v0 = canvasElement.transform.TransformPoint(vertices[triangles[i]]);
                    Vector3 v1 = canvasElement.transform.TransformPoint(vertices[triangles[i + 1]]);
                    Vector3 v2 = canvasElement.transform.TransformPoint(vertices[triangles[i + 2]]);
                    Gizmos.DrawLine(v0, v1);
                    Gizmos.DrawLine(v1, v2);
                    Gizmos.DrawLine(v2, v0);
                }
            }
            if (mesh2 != null)
            {
                var vertices = mesh2.vertices;
                var triangles = mesh2.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    Vector3 v0 = canvasElement2.transform.TransformPoint(vertices[triangles[i]]);
                    Vector3 v1 = canvasElement2.transform.TransformPoint(vertices[triangles[i + 1]]);
                    Vector3 v2 = canvasElement2.transform.TransformPoint(vertices[triangles[i + 2]]);
                    Gizmos.DrawLine(v0, v1);
                    Gizmos.DrawLine(v1, v2);
                    Gizmos.DrawLine(v2, v0);
                }
            }
            if (mesh != null && mesh2 != null)
            {
                Debug.Log(UIBatchTool.MeshOverlaps(mesh, canvasElement.transform, mesh2, canvasElement2.transform));
            }
        }
    }
}
