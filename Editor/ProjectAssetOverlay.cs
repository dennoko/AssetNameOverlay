using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

[InitializeOnLoad]
public static class AssetNameOverlay
{
    // ホバー状態の管理
    private static string hoveredGuid = null;
    private static Rect hoveredItemRect;
    private static Rect fixedOverlayRect;
    private static GUIContent cachedContent = null;
    private static bool shouldDrawOverlay = false;
    private static float lastSeenItemY = -1f;

    // GUID -> GUIContent（表示ファイル名等）のキャッシュ（GC Alloc & 処理の最適化）
    private static readonly Dictionary<string, GUIContent> contentCache = new Dictionary<string, GUIContent>();

    // オーバーレイ用テキストスタイル
    private static GUIStyle overlayTextStyle = null;
    private static GUIStyle OverlayTextStyle
    {
        get
        {
            if (overlayTextStyle == null)
            {
                overlayTextStyle = new GUIStyle(EditorStyles.label)
                {
                    alignment = TextAnchor.MiddleCenter,
                    wordWrap = false
                };
            }
            overlayTextStyle.normal.textColor = EditorGUIUtility.isProSkin ? Color.white : Color.black;
            return overlayTextStyle;
        }
    }

    private const string MenuPath = "dennokoworks/Asset Name Overlay/Enabled";
    private const string PrefsKey = "dennokoworks.AssetNameOverlay.Enabled";
    private const string OldPrefsKey = "dennokoworks.ProjectAssetOverlay.Enabled";

    private static bool isEnabled = true;

    public static bool IsEnabled
    {
        get => isEnabled;
        set
        {
            if (isEnabled != value)
            {
                isEnabled = value;
                EditorPrefs.SetBool(PrefsKey, value);
            }
        }
    }

    [MenuItem(MenuPath)]
    private static void ToggleEnabled()
    {
        IsEnabled = !IsEnabled;
        if (!IsEnabled)
        {
            ClearHover();
        }
        EditorApplication.RepaintProjectWindow();
    }

    [MenuItem(MenuPath, true)]
    private static bool ToggleEnabledValidate()
    {
        Menu.SetChecked(MenuPath, IsEnabled);
        return true;
    }

    static AssetNameOverlay()
    {
        isEnabled = EditorPrefs.GetBool(PrefsKey, EditorPrefs.GetBool(OldPrefsKey, true));

        // Projectウィンドウのアイテム描画コールバック
        EditorApplication.projectWindowItemOnGUI -= OnProjectWindowItemGUI;
        EditorApplication.projectWindowItemOnGUI += OnProjectWindowItemGUI;

        // エディタの更新ループ（ウィンドウ外判定用）
        EditorApplication.update -= OnEditorUpdate;
        EditorApplication.update += OnEditorUpdate;

        // プロジェクト変更時のキャッシュクリア
        EditorApplication.projectChanged -= contentCache.Clear;
        EditorApplication.projectChanged += contentCache.Clear;
    }

    private static void OnEditorUpdate()
    {
        if (!IsEnabled)
        {
            if (!string.IsNullOrEmpty(hoveredGuid))
            {
                ClearHover();
            }
            return;
        }
        // ホバー中にマウスがProjectWindow外に出た場合は即座にクリア
        if (!string.IsNullOrEmpty(hoveredGuid))
        {
            var mouseOver = EditorWindow.mouseOverWindow;
            if (mouseOver == null || mouseOver.GetType().Name != "ProjectBrowser")
            {
                ClearHover();
                return;
            }
        }

        // ProjectBrowser上でマウス移動イベントが空白領域でも確実に発生するよう wantsMouseMove を有効化
        if (EditorWindow.mouseOverWindow != null && EditorWindow.mouseOverWindow.GetType().Name == "ProjectBrowser")
        {
            if (!EditorWindow.mouseOverWindow.wantsMouseMove)
            {
                EditorWindow.mouseOverWindow.wantsMouseMove = true;
            }
        }

        // 次のRepaintパスに向けて描画フラグを初期化
        shouldDrawOverlay = false;
    }

    private static void OnProjectWindowItemGUI(string guid, Rect selectionRect)
    {
        if (!IsEnabled) return;

        Event e = Event.current;
        if (e == null) return;

        // クリックやドラッグ開始時はオーバーレイを消す
        if (e.type == EventType.MouseDown || e.type == EventType.MouseDrag)
        {
            if (!string.IsNullOrEmpty(hoveredGuid))
            {
                ClearHover();
            }
            return;
        }

        Vector2 mousePos = e.mousePosition;

        // 1. マウスが現在のアイテム上にあるかチェック
        if (selectionRect.Contains(mousePos))
        {
            if (hoveredGuid != guid)
            {
                // 新しいアセットにホバーした瞬間
                hoveredGuid = guid;
                hoveredItemRect = selectionRect;

                // キャッシュから表示内容を取得
                cachedContent = GetOrCreateContent(guid);

                // 表示位置の決定と固定（ホバー開始時のマウス位置を基準に固定）
                if (cachedContent != null && !string.IsNullOrEmpty(cachedContent.text))
                {
                    Vector2 size = OverlayTextStyle.CalcSize(cachedContent);
                    float width = size.x + 14;
                    float height = size.y + 6;

                    // マウスカーソルがのっている Project タブの EditorWindow を取得
                    EditorWindow projectWindow = EditorWindow.mouseOverWindow;
                    if (projectWindow == null || projectWindow.GetType().Name != "ProjectBrowser")
                    {
                        if (EditorWindow.focusedWindow != null && EditorWindow.focusedWindow.GetType().Name == "ProjectBrowser")
                        {
                            projectWindow = EditorWindow.focusedWindow;
                        }
                    }

                    bool isRightHalf = false;
                    float tabWidth = 0f;

                    if (projectWindow != null)
                    {
                        tabWidth = projectWindow.position.width;
                        // Projectタブの中心X座標（スクリーン座標）
                        float tabCenterX = projectWindow.position.x + tabWidth * 0.5f;
                        // マウスカーソルのX座標（スクリーン座標）
                        float mouseScreenX = GUIUtility.GUIToScreenPoint(mousePos).x;
                        isRightHalf = mouseScreenX >= tabCenterX;
                    }
                    else
                    {
                        // フォールバック
                        float viewWidth = EditorGUIUtility.currentViewWidth;
                        tabWidth = viewWidth;
                        isRightHalf = (viewWidth > 0 && mousePos.x >= viewWidth * 0.5f);
                    }

                    // タブの右半分か左半分かによって右下か左下かを切り替え
                    float x;
                    if (isRightHalf)
                    {
                        // 右半分: 左下に出す
                        x = mousePos.x - width - 15;
                    }
                    else
                    {
                        // 左半分: 右下に出す
                        x = mousePos.x + 15;
                    }

                    // ウィンドウ枠外にはみ出さないようガード
                    if (tabWidth > 0)
                    {
                        if (x + width > tabWidth - 5) x = tabWidth - width - 5;
                        if (x < 5) x = 5;
                    }

                    float y = mousePos.y + 15;
                    fixedOverlayRect = new Rect(x, y, width, height);
                }

                // ホバー開始時のみ再描画を要求
                EditorApplication.RepaintProjectWindow();
            }
            else
            {
                // 同じアセット上: スクロール等によるアイテムRectの変化に追従
                hoveredItemRect = selectionRect;
            }
        }
        else
        {
            // このアイテム上にはマウスがない
            // もしこのアイテムが現在ホバー中のアイテムだった場合、領域外へ出たためホバー解除
            if (hoveredGuid == guid)
            {
                ClearHover();
            }
        }

        // 2. 描画処理: ホバー中アセット以降のコールバックで描画し、後続アセットによる上書きを防ぐ
        if (e.type == EventType.Repaint && cachedContent != null && !string.IsNullOrEmpty(cachedContent.text))
        {
            // 新しい描画フレームの先頭（Y座標が上に戻った）でフラグをリセット
            if (selectionRect.y < lastSeenItemY - 50f)
            {
                shouldDrawOverlay = false;
            }
            lastSeenItemY = selectionRect.y;

            if (hoveredGuid == guid)
            {
                shouldDrawOverlay = true;
            }

            if (shouldDrawOverlay)
            {
                // 左カラム/右カラムの境界（X座標が大幅に離れた場合）は別ビューとみなして描画停止
                if (Mathf.Abs(selectionRect.x - hoveredItemRect.x) > 200f)
                {
                    shouldDrawOverlay = false;
                }
                else
                {
                    Color prevColor = GUI.color;

                    // 完全不透明（Alpha 1.0）な枠線と背景を描画（下のアセットや文字を完全に隠す）
                    Color borderColor = EditorGUIUtility.isProSkin 
                        ? new Color(0.35f, 0.35f, 0.35f, 1.0f) 
                        : new Color(0.55f, 0.55f, 0.55f, 1.0f);
                    Color bgColor = EditorGUIUtility.isProSkin 
                        ? new Color(0.15f, 0.15f, 0.15f, 1.0f) 
                        : new Color(0.88f, 0.88f, 0.88f, 1.0f);

                    // 枠線（外側1px）
                    EditorGUI.DrawRect(fixedOverlayRect, borderColor);
                    // 完全不透明な背景本体（内側）
                    EditorGUI.DrawRect(new Rect(fixedOverlayRect.x + 1, fixedOverlayRect.y + 1, fixedOverlayRect.width - 2, fixedOverlayRect.height - 2), bgColor);

                    // テキストの描画
                    GUI.Label(fixedOverlayRect, cachedContent, OverlayTextStyle);

                    GUI.color = prevColor;
                }
            }
        }
    }

    private static void ClearHover()
    {
        shouldDrawOverlay = false;
        if (string.IsNullOrEmpty(hoveredGuid) && cachedContent == null) return;

        hoveredGuid = null;
        cachedContent = null;
        EditorApplication.RepaintProjectWindow();
    }

    private static GUIContent GetOrCreateContent(string guid)
    {
        if (string.IsNullOrEmpty(guid)) return GUIContent.none;

        if (contentCache.TryGetValue(guid, out var content))
        {
            return content;
        }

        string path = AssetDatabase.GUIDToAssetPath(guid);
        string fileName = System.IO.Path.GetFileName(path);

        content = new GUIContent(fileName);
        contentCache[guid] = content;
        return content;
    }
}

