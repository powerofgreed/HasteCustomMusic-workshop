using UnityEngine;

public static class InputHelper
{
    public static bool IsDown(this KeyCode keyCode)
    {
        return Input.GetKeyDown(keyCode);
    }

    public static bool IsPressed(this KeyCode keyCode)
    {
        return Input.GetKey(keyCode);
    }

    public static bool IsUp(this KeyCode keyCode)
    {
        return Input.GetKeyUp(keyCode);
    }

    // For modifier key combinations 
    public static bool IsKeyComboDown(KeyCode mainKey, bool requireShift = false, bool requireCtrl = false, bool requireAlt = false)
    {
        if (!Input.GetKeyDown(mainKey))
            return false;

        if (requireShift && !(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)))
            return false;

        if (requireCtrl && !(Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)))
            return false;

        if (requireAlt && !(Input.GetKey(KeyCode.LeftAlt) || Input.GetKey(KeyCode.RightAlt)))
            return false;

        return true;
    }
}