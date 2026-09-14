document.addEventListener("click", event => {
    const button = event.target.closest("[data-selection-css-class][data-selected]");
    if (!button) {
        return;
    }

    const selectionCssClass = button.dataset.selectionCssClass;
    const selected = button.dataset.selected === "true";
    document.querySelectorAll(`.${selectionCssClass}`).forEach(selection => {
        selection.checked = selected;
    });
});
