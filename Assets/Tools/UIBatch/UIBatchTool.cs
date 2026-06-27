using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace UIBatch
{
    public static class UIBatchTool
    {
        #region 合批要素

        /// <summary>
        /// 获取画布元素的主纹理 InstanceID
        /// </summary>
        public static int GetTextureID(ICanvasElement canvasElement)
        {
            if (canvasElement is Text)
            {
                var text = canvasElement as Text;
                return text.mainTexture.GetInstanceID();
            }
            if (canvasElement is TMPro.TextMeshProUGUI)
            {
                var text = canvasElement as TMPro.TextMeshProUGUI;
                return text.mainTexture.GetInstanceID();
            }
            if (canvasElement is Image)
            {
                var img = canvasElement as Image;
                return img.mainTexture.GetInstanceID();
            }
            return 0;
        }

        /// <summary>
        /// 获取画布元素的渲染材质 InstanceID（materialForRendering，含 Mask stencil 修改）
        /// </summary>
        public static int GetMaterialID(ICanvasElement canvasElement)
        {
            if (canvasElement is Text)
            {
                var text = canvasElement as Text;
                return text.material.GetInstanceID();
            }
            if (canvasElement is TMPro.TextMeshProUGUI)
            {
                var text = canvasElement as TMPro.TextMeshProUGUI;
                return text.materialForRendering.GetInstanceID();
            }
            if (canvasElement is Image)
            {
                var img = canvasElement as Image;
                return img.materialForRendering.GetInstanceID();
            }
            return 0;
        }

        /// <summary>
        /// 获取 Mask UnMask DC（popMaterial[0]）的材质 InstanceID
        /// </summary>
        public static int GetMaterialIDUnmask(ICanvasElement canvasElement)
        {
            var canvasRender = canvasElement.transform.GetComponent<CanvasRenderer>();
            if (canvasRender.popMaterialCount > 0)
                return canvasRender.GetPopMaterial(0).GetInstanceID();
            return 0;
        }

        #endregion

        #region 扩展合批键辅助方法

        /// <summary>
        /// 获取 transform 的直属 Canvas（即从 transform 向上找到的第一个已启用 Canvas）。
        /// 嵌套 Canvas 会被优先返回，从而将其子元素与根 Canvas 元素区分开来，避免跨 Canvas 合批。
        /// </summary>
        /// <param name="transform">目标节点</param>
        /// <param name="rootCanvasTransform">根 Canvas 节点（作为向上搜索的上界）</param>
        public static Canvas GetDirectCanvas(Transform transform, Transform rootCanvasTransform)
        {
            var parent = transform.parent;
            while (parent != null)
            {
                // 找到第一个已启用的 Canvas 即为直属 Canvas（含根 Canvas 本身）
                var c = parent.GetComponent<Canvas>();
                if (c != null && c.enabled)
                    return c;
                if (parent == rootCanvasTransform)
                    break;
                parent = parent.parent;
            }
            // 未找到嵌套 Canvas，fallback 到根 Canvas
            return rootCanvasTransform != null
                ? rootCanvasTransform.GetComponent<Canvas>()
                : null;
        }

        /// <summary>
        /// 计算 transform 的 Mask 嵌套深度（= 祖先层级中已启用 Mask 组件的数量）。
        /// 该值对应 Unity Stencil 引用值，不同深度的元素使用不同 Stencil 状态，无法合批。
        /// </summary>
        /// <param name="transform">目标节点</param>
        /// <param name="rootCanvasTransform">根 Canvas 节点（作为向上搜索的上界）</param>
        public static int GetStencilDepth(Transform transform, Transform rootCanvasTransform)
        {
            int depth = 0;
            var parent = transform.parent;
            while (parent != null && parent != rootCanvasTransform)
            {
                var mask = parent.GetComponent<Mask>();
                if (mask != null && mask.enabled)
                    depth++;
                parent = parent.parent;
            }
            return depth;
        }

        /// <summary>
        /// 生成 ElementNode 的合批键摘要字符串，用于日志输出。
        /// 涵盖 Canvas、Shader、Material、RenderQueue、Texture、Stencil、RectMask2D 维度。
        /// </summary>
        public static string GetBatchKeySummary(ElementNode node)
        {
            string shaderPart = !string.IsNullOrEmpty(node.shaderName)
                ? $" shader:\"{node.shaderName}\""
                : string.Empty;
            string rectMaskPart = node.rectMask2d != null
                ? $" rectMask:\"{node.rectMask2d.name}\""
                : string.Empty;
            return $"canvas:{node.canvasID} | mat:{node.materialID}{shaderPart} rq:{node.renderQueue}"
                 + $" | tex:{node.textureID} | stencil:{node.stencilDepth}{rectMaskPart}";
        }

        #endregion

        //Canvas.willRenderCanvases += DoRebuilds; // 这个事件在每次渲染前都会被调用, 可以用来处理批处理逻辑
        //确保在渲染后比较元素的mesh
        /// <summary>
        /// 判断两个画布元素的 mesh 是否相交，先用 mesh 三角形精确判断
        /// </summary>
        public static bool Overlaps(ICanvasElement canvasElement1, ICanvasElement canvasElement2)
        {
            if (canvasElement1 == null || canvasElement2 == null)
                return false;
            var trm1 = canvasElement1.transform;
            var trm2 = canvasElement2.transform;
            if (trm1.gameObject.activeSelf == false || trm2.gameObject.activeSelf == false)
                return false;
            var canvasrender1 = trm1.gameObject.GetComponent<CanvasRenderer>();
            var canvasrender2 = trm2.gameObject.GetComponent<CanvasRenderer>();
            if (canvasrender1 == null || canvasrender2 == null)
                return false;
            var mesh1 = GetGraphicMesh(canvasrender1.transform.GetComponent<Graphic>());
            var mesh2 = GetGraphicMesh(canvasrender2.transform.GetComponent<Graphic>());
            return MeshOverlaps(mesh1, trm1, mesh2, trm2);
        }

        static List<Vector3> temp = new List<Vector3>(128);
        static List<int> ints = new List<int>(128);
        public static Mesh GetGraphicMesh(Graphic graphic)
        {
            if (graphic == null) return null;
            var mesh = new Mesh();
            if (graphic is TMPro.TextMeshProUGUI tmpro)
            {
                //获取字体顶点真实的 mesh
                var rawMesh = tmpro.mesh;
                var textInfo = tmpro.textInfo;
                rawMesh.MarkDynamic();
                rawMesh.vertices = textInfo.meshInfo[0].vertices;
                rawMesh.uv = textInfo.meshInfo[0].uvs0;
                rawMesh.uv2 = textInfo.meshInfo[0].uvs2;
                rawMesh.triangles = textInfo.meshInfo[0].triangles;
                // 重新过滤零点和越界索引，避免因重复顶点导致的相交误判
                temp.Clear();
                ints.Clear();
                foreach (var item in rawMesh.vertices)
                {
                    if (item == Vector3.zero)
                        continue;
                    temp.Add(item);
                }
                foreach (var item in rawMesh.triangles)
                {
                    if (item > temp.Count - 1)
                        continue;
                    ints.Add(item);
                }
                mesh.vertices = temp.ToArray();
                mesh.triangles = ints.ToArray();
                return mesh;
            }
            var method = GetOnPopulateMeshMethod();
            var vh = new VertexHelper();
            method.Invoke(graphic, new object[] { vh });
            vh.FillMesh(mesh);
            return mesh;
        }

        private static MethodInfo _onPopulateMeshMethod;

        /// <summary>
        /// 获取并缓存 Graphic.OnPopulateMesh 方法的反射信息
        /// </summary>
        public static MethodInfo GetOnPopulateMeshMethod()
        {
            if (_onPopulateMeshMethod == null)
            {
                var graphicType = typeof(Graphic);
                _onPopulateMeshMethod = graphicType.GetMethod(
                    "OnPopulateMesh",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                    null,
                    new[] { typeof(VertexHelper) },
                    null
                );
            }
            return _onPopulateMeshMethod;
        }

        /// <summary>
        /// 精确判断两个 mesh 是否相交（逐三角形 SAT 检测）
        /// </summary>
        public static bool MeshOverlaps(Mesh mesh1, Transform trm1, Mesh mesh2, Transform trm2)
        {
            if (mesh1 == null || mesh2 == null) return false;
            var vertices1 = mesh1.vertices;
            var vertices2 = mesh2.vertices;
            var indices1 = mesh1.triangles;
            var indices2 = mesh2.triangles;

            // 将顶点从本地坐标转换到世界坐标
            Vector3[] worldVerts1 = new Vector3[vertices1.Length];
            for (int i = 0; i < vertices1.Length; i++)
                worldVerts1[i] = trm1.TransformPoint(vertices1[i]);
            Vector3[] worldVerts2 = new Vector3[vertices2.Length];
            for (int i = 0; i < vertices2.Length; i++)
                worldVerts2[i] = trm2.TransformPoint(vertices2[i]);

            for (int i = 0; i < indices1.Length; i += 3)
            {
                Vector2[] tri1 = new Vector2[3]
                {
                    (Vector2)worldVerts1[indices1[i]],
                    (Vector2)worldVerts1[indices1[i + 1]],
                    (Vector2)worldVerts1[indices1[i + 2]]
                };
                for (int j = 0; j < indices2.Length; j += 3)
                {
                    Vector2[] tri2 = new Vector2[3]
                    {
                        (Vector2)worldVerts2[indices2[j]],
                        (Vector2)worldVerts2[indices2[j + 1]],
                        (Vector2)worldVerts2[indices2[j + 2]]
                    };
                    if (!IsTri(tri1) || !IsTri(tri2))
                        continue;
                    if (TrianglesOverlap(tri1, tri2))
                        return true;
                }
            }
            return false;
        }

        public static bool IsTri(Vector2[] tri1)
        {
            if (tri1.Length != 3)
                return false;
            if (tri1[0] == tri1[1] || tri1[1] == tri1[2] || tri1[0] == tri1[2])
                return false;
            return true;
        }

        /// <summary>
        /// 判断两个三角形是否相交（边相交 + 包含检测）
        /// </summary>
        public static bool TrianglesOverlap(Vector2[] tri1, Vector2[] tri2)
        {
            // 检查三角形1的边与三角形2的边是否相交
            for (int i = 0; i < 3; i++)
            {
                Vector2 a1 = tri1[i];
                Vector2 a2 = tri1[(i + 1) % 3];
                for (int j = 0; j < 3; j++)
                {
                    Vector2 b1 = tri2[j];
                    Vector2 b2 = tri2[(j + 1) % 3];
                    if (LinesIntersect(a1, a2, b1, b2))
                        return true;
                }
            }
            // 检查三角形1的顶点是否在三角形2内
            for (int i = 0; i < 3; i++)
            {
                if (PointInTriangle(tri1[i], tri2))
                    return true;
            }
            // 检查三角形2的顶点是否在三角形1内
            for (int i = 0; i < 3; i++)
            {
                if (PointInTriangle(tri2[i], tri1))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 判断点是否在三角形内（叉积符号法）
        /// </summary>
        private static bool PointInTriangle(Vector2 pt, Vector2[] tri)
        {
            float d1 = Cross(tri[1] - tri[0], pt - tri[0]);
            float d2 = Cross(tri[2] - tri[1], pt - tri[1]);
            float d3 = Cross(tri[0] - tri[2], pt - tri[2]);
            bool has_neg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            bool has_pos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(has_neg && has_pos);
        }

        /// <summary>
        /// 判断两线段是否相交
        /// </summary>
        private static bool LinesIntersect(Vector2 p1, Vector2 p2, Vector2 q1, Vector2 q2)
        {
            float o1 = Cross(p2 - p1, q1 - p1);
            float o2 = Cross(p2 - p1, q2 - p1);
            float o3 = Cross(q2 - q1, p1 - q1);
            float o4 = Cross(q2 - q1, p2 - q1);
            return (o1 * o2 < 0) && (o3 * o4 < 0);
        }

        private static float Cross(Vector2 a, Vector2 b)
        {
            return a.x * b.y - a.y * b.x;
        }
    }
}
