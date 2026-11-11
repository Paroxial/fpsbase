using UnityEngine;

namespace FpsBase
{
    public static class CursorCapture
    {
        public static void Set(bool captured)
        {
            Cursor.lockState = captured ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !captured;
        }
    }
}
