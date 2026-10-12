using System;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Loamlight.UI
{
    /// <summary>One clickable item slot (inventory, chest, trash or a recipe).</summary>
    public sealed class SlotView : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        public Image frame, icon;
        public Text count;
        public Action<PointerEventData.InputButton> onClick;
        public Action onEnter, onExit;

        public void OnPointerClick(PointerEventData e) => onClick?.Invoke(e.button);
        public void OnPointerEnter(PointerEventData e) => onEnter?.Invoke();
        public void OnPointerExit(PointerEventData e) => onExit?.Invoke();
    }
}
