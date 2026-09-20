using System;
using Sdk;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace StudentAgeDialogueSave.UI
{
    internal static class NativeUiCloner
    {
        internal static GameObject Clone(GameObject source, Transform parent, string name)
        {
            if (source == null) throw new InvalidOperationException("原版 UI 模板不可用：" + name);
            var clone = Object.Instantiate(source, parent, false);
            clone.name = name;
            ResetListeners(clone);
            return clone;
        }

        // UICellPool moves templates under its unscaled pool root with worldPositionStays.
        // Reproduce UIItemGroup.SetData when bringing a cell back into a Canvas.
        internal static GameObject CloneCell(GameObject source, Transform parent, string name)
        {
            if (source == null) throw new InvalidOperationException("原版 UI 单元不可用：" + name);
            var clone = Object.Instantiate(source);
            clone.name = name;
            ResetListeners(clone);
            var rect = clone.GetComponent<RectTransform>();
            rect.SetParent(parent, true);
            rect.localScale = Vector3.one;
            rect.localRotation = Quaternion.identity;
            rect.anchoredPosition3D = Vector3.zero;
            return clone;
        }

        private static void ResetListeners(GameObject clone)
        {
            // Never inherit a cloned native slot's file-operation listeners.
            foreach (var button in clone.GetComponentsInChildren<Button>(true))
                button.onClick = new Button.ButtonClickedEvent();
            foreach (var toggle in clone.GetComponentsInChildren<Toggle>(true))
            {
                toggle.onValueChanged = new Toggle.ToggleEvent();
                toggle.group = null;
            }
        }

        internal static GameObject EmptyContainer(RectTransform source, string name)
        {
            var clone = Clone(source.gameObject, source.parent, name);
            foreach (Transform child in clone.transform)
            {
                child.gameObject.SetActive(false);
                Object.Destroy(child.gameObject);
            }
            return clone;
        }

        internal static UIButton CloneButton(UIButton source, string name, Action action)
        {
            var clone = Clone(source.gameObject, source.transform.parent, name);
            var button = new UIButton(clone);
            button.SetSound(source.soundChannel, source.clickSoundUrl, source.clickSoundVolumn,
                source.hoverSoundUrl, source.hoverSoundVolumn);
            button.AddClick(() => action());
            clone.SetActive(true);
            return button;
        }

        internal static void Remove(GameObject gameObject)
        {
            if (gameObject == null) return;
            gameObject.SetActive(false);
            Object.Destroy(gameObject);
        }
    }
}
