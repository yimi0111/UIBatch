using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace UIBatch
{
    /// <summary>
    /// 断批原因枚举。
    /// 数值越小优先级越高：多重差异并存时，优先报告编号最小（最根本）的原因。
    /// </summary>
    public enum BatchBreakReason
    {
        /// <summary>无断批（与上一元素同批）</summary>
        None = 0,
        /// <summary>首个批次，无前序参照</summary>
        FirstBatch = 1,
        /// <summary>所属渲染 Canvas 不同（嵌套 Canvas 各自独立批次，最高优先级）</summary>
        DifferentCanvas = 10,
        /// <summary>materialForRendering 的 RenderQueue 不同</summary>
        DifferentRenderQueue = 20,
        /// <summary>Shader 不同（比材质实例差异更根本）</summary>
        DifferentShader = 30,
        /// <summary>材质实例不同（同 Shader 但属性/纹理参数不同）</summary>
        DifferentMaterial = 35,
        /// <summary>主纹理 (mainTexture) 不同</summary>
        DifferentTexture = 40,
        /// <summary>Mask/Stencil 嵌套深度不同</summary>
        DifferentStencilDepth = 50,
        /// <summary>RectMask2D 裁剪区域不同</summary>
        DifferentRectMask = 55,
        /// <summary>渲染深度不同（相交几何导致的分层）</summary>
        DifferentDepth = 70,
    }

    /// <summary>
    /// UI 元素节点，保存合批分析所需的全部状态。
    /// v2 新增：canvasID / stencilDepth / renderQueue / shaderName /
    ///          sortingLayerID / sortingOrder / breakReason / breakReasonDetail
    /// </summary>
    public class ElementNode
    {
        public ElementNode parent;
        public Canvas rootCanvas;
        public Mask firstMask;
        public RectMask2D rectMask2d;
        public Transform transform;
        public int order;
        public int depth;
        public int materialID;
        public int textureID;
        public int batchID;

        // ---------- 扩展合批键字段 ----------
        /// <summary>直属 Canvas 的 InstanceID。不同 Canvas 下的元素无法合批。</summary>
        public int canvasID;
        /// <summary>Mask 嵌套层数（= 祖先中已启用的 Mask 组件数量），决定 Stencil 引用值。</summary>
        public int stencilDepth;
        /// <summary>materialForRendering 的 renderQueue。不同值无法合批。</summary>
        public int renderQueue;
        /// <summary>materialForRendering 的 Shader 名称，用于区分断批主因。</summary>
        public string shaderName;
        /// <summary>所在 Canvas 的 sortingLayerID（含父级 Canvas 覆盖）。</summary>
        public int sortingLayerID;
        /// <summary>所在 Canvas 的 sortingOrder。</summary>
        public int sortingOrder;

        // ---------- 断批原因（顺序扫描分组时填充）----------
        /// <summary>该节点触发新批次的主要原因；None 表示与前一节点同批。</summary>
        public BatchBreakReason breakReason;
        /// <summary>断批原因详情文本，可包含多重原因描述。</summary>
        public string breakReasonDetail;
    }

    public static class UIBatctEditor
    {
        [MenuItem("UITool/UIBatch")]
        public static void UIBatchDebug()
        {
            var transform = Selection.activeTransform;
            if (transform == null)
                return;
            new UIBatch().TraverseAllUINodes(transform);
        }
    }

    public class UIBatch
    {
        public List<ElementNode> elementNodes = new List<ElementNode>();
        private Canvas rootCanvas;

        public void TraverseAllUINodes(Transform transform)
        {
            var root = GetParentCanvas(transform);
            if (root == null)
                return;
            rootCanvas = root.GetComponent<Canvas>();
            var keyValuePairs = new Dictionary<Transform, int>();
            var maskNodes = new List<ElementNode>();
            int order = 0;
            elementNodes.Clear();
            Traverse(keyValuePairs, root, ref order);

            // 第一遍：填充材质、纹理及扩展合批键；为 Mask 节点生成 UnMask 节点
            foreach (var item in elementNodes)
            {
                if (!IsDrawable(item))
                {
                    item.depth = -1;
                    continue;
                }
                // 带 Mask 的节点会产生两个 DC（写 stencil + 清 stencil）
                if (item.transform.GetComponent<Mask>() != null &&
                    item.transform.GetComponent<Mask>().enabled)
                {
                    var newNode = new ElementNode();
                    newNode.transform = item.transform;
                    newNode.order = item.order;
                    newNode.depth = item.depth;
                    maskNodes.Add(newNode);
                }
                var graphic = item.transform.GetComponent<Graphic>();
                item.materialID = UIBatchTool.GetMaterialID(graphic);
                item.textureID = UIBatchTool.GetTextureID(graphic);
                FillExtendedBatchKey(item, graphic);
            }

            // 填充 UnMask 节点的合批键
            foreach (var item in maskNodes)
            {
                if (!IsDrawable(item))
                    item.depth = -1;
                if (item.transform.GetComponent<Mask>().enabled == false)
                    item.depth = -1;
                var graphic = item.transform.GetComponent<Graphic>();
                item.materialID = UIBatchTool.GetMarterIalIDUnmask(graphic);
                item.textureID = UIBatchTool.GetTextureID(graphic);
                FillExtendedBatchKey(item, graphic);
            }

            // 第二遍：按相交关系计算渲染深度（depth）
            for (int i = 0; i < elementNodes.Count; i++)
            {
                if (elementNodes[i].depth == -1)
                    continue;
                for (int j = 0; j < i; j++)
                {
                    if (elementNodes[j].depth == -1)
                        continue;
                    if (UIBatchTool.Overlaps(
                            elementNodes[i].transform.GetComponent<Graphic>(),
                            elementNodes[j].transform.GetComponent<Graphic>()))
                    {
                        if (CanBatch(elementNodes[i], elementNodes[j]))
                        {
                            // 可合批：取最大深度，不需要增加层级
                            elementNodes[i].depth =
                                Mathf.Max(elementNodes[i].depth, elementNodes[j].depth);
                        }
                        else
                        {
                            // 不可合批：当前元素深度至少比被相交元素大 1
                            elementNodes[i].depth =
                                Mathf.Max(elementNodes[i].depth, elementNodes[j].depth + 1);
                            // 若被相交的是 Mask 节点（且不是自己的 Mask 祖先），
                            // Mask 本身有两个 DC，还需额外 +1
                            var mask = elementNodes[j].transform.GetComponent<Mask>();
                            if (mask != null && mask.enabled &&
                                elementNodes[i].firstMask != mask)
                            {
                                elementNodes[i].depth += 1;
                            }
                        }
                    }
                }
            }

            // 计算 UnMask DC 的深度：其所有子节点最大深度 + 1
            for (int i = 0; i < maskNodes.Count; i++)
            {
                var maskNode = maskNodes[i];
                int maxChildDepth = -1;
                foreach (var item in elementNodes)
                {
                    if (item.firstMask != null &&
                        item.firstMask.transform == maskNode.transform)
                    {
                        maxChildDepth = Mathf.Max(maxChildDepth, item.depth);
                    }
                }
                maskNode.depth = maxChildDepth + 1;
            }

            foreach (var item in maskNodes)
            {
                if (IsDrawable(item))
                    elementNodes.Add(item);
            }

            // 排序：深度 → 材质 → 纹理 → 遍历顺序
            elementNodes.Sort(Sort);

            // 第三遍：顺序扫描分组模拟，记录断批原因
            AssignBatchIDs();

            // 输出分析结果（批次摘要 + 逐元素日志）
            PrintBatchResults();
        }

        /// <summary>
        /// 填充 ElementNode 的扩展合批键字段：canvasID、stencilDepth、renderQueue、shaderName 等。
        /// </summary>
        private void FillExtendedBatchKey(ElementNode node, Graphic graphic)
        {
            if (graphic == null)
                return;

            // 直属 Canvas（嵌套 Canvas 使元素进入独立批次）
            var canvas = UIBatchTool.GetDirectCanvas(graphic.transform, rootCanvas.transform);
            if (canvas != null)
            {
                node.canvasID = canvas.GetInstanceID();
                node.sortingLayerID = canvas.sortingLayerID;
                node.sortingOrder = canvas.sortingOrder;
            }
            else
            {
                node.canvasID = rootCanvas.GetInstanceID();
                node.sortingLayerID = rootCanvas.sortingLayerID;
                node.sortingOrder = rootCanvas.sortingOrder;
            }

            // Mask 嵌套深度（= 祖先中已启用的 Mask 数量）
            node.stencilDepth = UIBatchTool.GetStencilDepth(graphic.transform, rootCanvas.transform);

            // 材质的 RenderQueue 与 Shader（基于 materialForRendering，含 Mask stencil 修改）
            var mat = graphic.materialForRendering;
            if (mat != null)
            {
                node.renderQueue = mat.renderQueue;
                node.shaderName = mat.shader != null ? mat.shader.name : string.Empty;
            }
        }

        /// <summary>
        /// 顺序扫描所有有效节点，分配 batchID 并记录每个新批次的断批原因。
        /// 模拟 Unity 实际批处理的"顺序扫描 + 分组"行为。
        /// </summary>
        private void AssignBatchIDs()
        {
            int batchID = 0;
            ElementNode prevValidNode = null;

            for (int i = 0; i < elementNodes.Count; i++)
            {
                var cur = elementNodes[i];
                if (cur.depth == -1)
                    continue;

                if (prevValidNode == null)
                {
                    // 首个有效节点，开启第 0 批
                    cur.batchID = batchID;
                    cur.breakReason = BatchBreakReason.FirstBatch;
                    cur.breakReasonDetail = "首个批次";
                    prevValidNode = cur;
                    continue;
                }

                BatchBreakReason reason;
                string detail;
                bool canBatch = CanBatchWithReason(cur, prevValidNode, out reason, out detail);

                // 深度不同也会断批；若同时存在合批键差异，报告更根本的原因并将深度差异降为次因
                if (cur.depth != prevValidNode.depth)
                {
                    if (!canBatch)
                    {
                        // 多重原因：合批键差异为主因，深度差异为次因
                        detail = $"[主因] {detail}; [次因] 渲染深度不同 ({prevValidNode.depth}→{cur.depth})";
                    }
                    else
                    {
                        reason = BatchBreakReason.DifferentDepth;
                        detail = $"渲染深度不同: {prevValidNode.depth} → {cur.depth}";
                        canBatch = false;
                    }
                }

                if (!canBatch)
                {
                    batchID++;
                    cur.breakReason = reason;
                    cur.breakReasonDetail = detail;
                }
                else
                {
                    cur.breakReason = BatchBreakReason.None;
                    cur.breakReasonDetail = string.Empty;
                }

                cur.batchID = batchID;
                prevValidNode = cur;
            }
        }

        /// <summary>
        /// 按批次分组打印分析结果：
        ///   每批输出元素数、BatchKey 摘要、断批原因；
        ///   批次内逐元素输出名称、depth、order。
        /// </summary>
        private void PrintBatchResults()
        {
            var batches = new Dictionary<int, List<ElementNode>>();
            foreach (var item in elementNodes)
            {
                if (item.depth == -1)
                    continue;
                if (!batches.ContainsKey(item.batchID))
                    batches[item.batchID] = new List<ElementNode>();
                batches[item.batchID].Add(item);
            }

            Debug.Log($"===== UIBatch 分析结果：共 {batches.Count} 个 DrawCall =====");
            foreach (var kv in batches)
            {
                var list = kv.Value;
                var rep = list[0]; // 代表元素，用于 BatchKey 摘要
                string keySummary = UIBatchTool.GetBatchKeySummary(rep);

                string breakInfo = string.Empty;
                if (rep.breakReason != BatchBreakReason.None &&
                    rep.breakReason != BatchBreakReason.FirstBatch)
                {
                    breakInfo = $"\n  ↳ 断批原因: [{rep.breakReason}] {rep.breakReasonDetail}";
                }

                Debug.Log($"[Batch {kv.Key}] 元素数:{list.Count} | {keySummary}{breakInfo}");
                foreach (var item in list)
                {
                    Debug.Log($"  └─ {item.transform.name}  depth:{item.depth}  order:{item.order}");
                }
            }
        }

        private int Sort(ElementNode a, ElementNode b)
        {
            if (a.depth != b.depth)
                return a.depth < b.depth ? -1 : 1;
            if (a.materialID != b.materialID)
                return a.materialID < b.materialID ? -1 : 1;
            if (a.textureID != b.textureID)
                return a.textureID < b.textureID ? -1 : 1;
            return a.order < b.order ? -1 : 1;
        }

        /// <summary>
        /// 判断两元素是否可合批（不含深度判断）。内部委托给 CanBatchWithReason。
        /// </summary>
        private bool CanBatch(ElementNode a, ElementNode b)
        {
            if (a.depth == -1 || b.depth == -1)
                return true;
            BatchBreakReason reason;
            string detail;
            return CanBatchWithReason(a, b, out reason, out detail);
        }

        /// <summary>
        /// 判断两元素是否可合批，并以 out 参数返回断批的主因及详情文本。
        /// 检查顺序即为优先级顺序（数值越小优先级越高）。
        /// </summary>
        private bool CanBatchWithReason(ElementNode a, ElementNode b,
            out BatchBreakReason reason, out string detail)
        {
            reason = BatchBreakReason.None;
            detail = string.Empty;

            // 优先级 1 (10)：必须在同一直属 Canvas 下
            if (a.canvasID != b.canvasID)
            {
                reason = BatchBreakReason.DifferentCanvas;
                detail = "所属渲染 Canvas 不同";
                return false;
            }

            // 优先级 2 (20)：materialForRendering 的 RenderQueue 必须相同
            if (a.renderQueue != b.renderQueue)
            {
                reason = BatchBreakReason.DifferentRenderQueue;
                detail = $"RenderQueue 不同: {b.renderQueue} → {a.renderQueue}";
                return false;
            }

            // 优先级 3 (30)：Shader 不同（比材质实例差异更根本）
            if (a.shaderName != b.shaderName)
            {
                reason = BatchBreakReason.DifferentShader;
                detail = $"Shader 不同: \"{b.shaderName}\" → \"{a.shaderName}\"";
                return false;
            }

            // 优先级 4 (35)：材质实例不同（同 Shader 但属性/纹理参数不同）
            if (a.materialID != b.materialID)
            {
                reason = BatchBreakReason.DifferentMaterial;
                detail = $"材质实例不同 (matId {b.materialID} → {a.materialID})";
                return false;
            }

            // 优先级 5 (40)：主纹理不同
            if (a.textureID != b.textureID)
            {
                reason = BatchBreakReason.DifferentTexture;
                detail = $"主纹理不同 (texId {b.textureID} → {a.textureID})";
                return false;
            }

            // 优先级 6 (50)：Stencil/Mask 嵌套深度不同
            if (a.stencilDepth != b.stencilDepth)
            {
                reason = BatchBreakReason.DifferentStencilDepth;
                detail = $"Mask/Stencil 深度不同: {b.stencilDepth} → {a.stencilDepth}";
                return false;
            }

            // 优先级 7 (55)：RectMask2D 裁剪区域不同
            if (a.rectMask2d != b.rectMask2d)
            {
                reason = BatchBreakReason.DifferentRectMask;
                var aName = a.rectMask2d != null ? a.rectMask2d.name : "(无)";
                var bName = b.rectMask2d != null ? b.rectMask2d.name : "(无)";
                detail = $"RectMask2D 不同: \"{bName}\" → \"{aName}\"";
                return false;
            }

            return true;
        }

        private Transform GetParentCanvas(Transform node)
        {
            if (node.GetComponent<Canvas>() != null && node.GetComponent<Canvas>().enabled)
                return node;
            while (node.parent != null)
            {
                node = node.parent;
                if (node.GetComponent<Canvas>() != null && node.GetComponent<Canvas>().enabled)
                    return node;
            }
            return null;
        }

        private void Traverse(Dictionary<Transform, int> dict, Transform node, ref int order)
        {
            if (node.GetComponent<CanvasRenderer>() != null)
            {
                var elementNode = new ElementNode();
                elementNode.transform = node;
                elementNode.order = order;
                // 记录第一个 Mask 祖先（用于 UnMask DC 归属判断）
                if (node.GetComponentInParent<Mask>() != null)
                    elementNode.firstMask = GetFirstEnableMask(node.transform, rootCanvas.transform);
                // 记录直属 RectMask2D（排除自身上挂载的 RectMask2D）
                if (node.GetComponentInParent<RectMask2D>() != null &&
                    node.GetComponent<RectMask2D>() != node.GetComponentInParent<RectMask2D>())
                    elementNode.rectMask2d = GetFirstEnableRectMask(node.transform, rootCanvas.transform);
                dict.Add(node, order++);
                elementNodes.Add(elementNode);
            }
            for (int i = 0; i < node.childCount; i++)
                Traverse(dict, node.GetChild(i), ref order);
        }

        public static Mask GetFirstEnableMask(Transform transform, Transform canvas)
        {
            if (transform == null)
                return null;
            var mask = transform.GetComponent<Mask>();
            if (mask != null && mask.enabled)
                return mask;
            var parent = transform.parent;
            while (parent != null && parent != canvas)
            {
                mask = parent.GetComponent<Mask>();
                if (mask != null && mask.enabled)
                    return mask;
                parent = parent.parent;
            }
            return null;
        }

        public static RectMask2D GetFirstEnableRectMask(Transform transform, Transform canvas)
        {
            if (transform == null)
                return null;
            var parent = transform.parent;
            while (parent != null && parent != canvas)
            {
                var mask2D = parent.GetComponent<RectMask2D>();
                if (mask2D != null && mask2D.enabled)
                    return mask2D;
                parent = parent.parent;
            }
            return null;
        }

        public static bool IsDrawable(ElementNode node)
        {
            var graphic = node.transform.GetComponent<Graphic>();
            if (graphic == null)
                return false;
            if (!graphic.enabled)
                return false;
            if (!graphic.IsActive() || !graphic.gameObject.activeInHierarchy)
                return false;
            if (graphic.color.a <= 0f)
                return false;
            if (graphic.transform.lossyScale.x == 0 || graphic.transform.lossyScale.y == 0)
                return false;
            // 不在 RectMask2D 显示区域内的元素不参与渲染
            if (node.rectMask2d != null && node.rectMask2d.isActiveAndEnabled &&
                !UIBatchTool.Overlaps(graphic, node.rectMask2d.GetComponent<Graphic>()))
                return false;
            // 无内容的 TMP 文本不渲染
            var text = node.transform.GetComponent<TMPro.TextMeshProUGUI>();
            if (text != null && text.text == string.Empty)
                return false;
            return true;
        }

        public static bool IsDrawable(Graphic graphic)
        {
            if (graphic == null)
                return false;
            if (!graphic.enabled)
                return false;
            if (!graphic.IsActive() || !graphic.gameObject.activeInHierarchy)
                return false;
            if (graphic.color.a <= 0f)
                return false;
            if (graphic.transform.lossyScale.x == 0 || graphic.transform.lossyScale.y == 0)
                return false;
            return true;
        }
    }
}
