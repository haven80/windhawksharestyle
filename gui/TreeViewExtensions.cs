using System.Runtime.InteropServices;

namespace WindhawkShare.Gui;

/// <summary>Hides the checkbox of individual nodes (note rows, "loading...").</summary>
internal static class TreeViewExtensions
{
    private const int TVIF_HANDLE = 0x10;
    private const int TVIF_STATE = 0x08;
    private const int TVIS_STATEIMAGEMASK = 0xF000;
    private const int TVM_SETITEMW = 0x1100 + 63;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TVITEM
    {
        public int mask;
        public IntPtr hItem;
        public int state;
        public int stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public int iSelectedImage;
        public int cChildren;
        public IntPtr lParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TVITEM lParam);

    public static void HideCheckBox(this TreeNode node)
    {
        var tree = node.TreeView;
        if (tree is null || !tree.IsHandleCreated) return;
        var item = new TVITEM
        {
            mask = TVIF_HANDLE | TVIF_STATE,
            hItem = node.Handle,
            stateMask = TVIS_STATEIMAGEMASK,
            state = 0,
        };
        SendMessage(tree.Handle, TVM_SETITEMW, IntPtr.Zero, ref item);
    }
}
