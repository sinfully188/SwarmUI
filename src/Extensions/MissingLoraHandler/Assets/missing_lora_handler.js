(function () {
    let state = {
        pending: new Map(),
        requestId: 0,
        refreshTimer: null,
    };

    function queueRefresh() {
        if (state.pending.size > 0 || state.refreshTimer) {
            return;
        }
        state.refreshTimer = setTimeout(() => {
            state.refreshTimer = null;
            refreshParameterValues(true, null, () => {
                loraHelper.loadFromParams();
                sdLoraBrowser.rebuildSelectedClasses();
            });
        }, 10);
    }

    function fixMissingLora(lora) {
        if (!lora?.missing || state.pending.has(lora.name)) {
            return;
        }
        let originalName = lora.name;
        let requestId = ++state.requestId;
        state.pending.set(originalName, requestId);
        loraHelper.rebuildUI();
        genericRequest('ResolveMissingLora', { loraName: originalName }, data => {
            if (state.pending.get(originalName) !== requestId) {
                return;
            }
            state.pending.delete(originalName);
            let resolved = loraHelper.replaceSelectedLora(originalName, data.resolved_name, {
                name: data.resolved_name,
                lora_default_weight: data.lora_default_weight,
                lora_default_confinement: data.lora_default_confinement,
            });
            if (resolved) {
                doNoticePopover(`Fixed LoRA: ${cleanModelName(resolved.name)}`, 'notice-pop-green');
                sdLoraBrowser.rebuildSelectedClasses();
            }
            queueRefresh();
        }, 0, e => {
            if (state.pending.get(originalName) !== requestId) {
                return;
            }
            state.pending.delete(originalName);
            loraHelper.rebuildUI();
            queueRefresh();
            showError(`Unable to auto-fix LoRA '${originalName}': ${e}`);
        });
    }

    function renderAction(container, lora) {
        if (!lora?.missing) {
            return;
        }
        let button = document.createElement('button');
        button.className = 'basic-button lora-missing-fix-button';
        button.innerText = state.pending.has(lora.name) ? 'Searching...' : 'Auto Find/Fix';
        button.disabled = state.pending.has(lora.name);
        button.title = 'Search the current LoRA folders for a matching filename and replace this missing entry.';
        button.addEventListener('click', () => fixMissingLora(lora));
        container.appendChild(button);
    }

    function registerHook() {
        if (!window.loraHelper?.registerChipActionRenderer) {
            setTimeout(registerHook, 100);
            return;
        }
        loraHelper.registerChipActionRenderer((container, lora) => renderAction(container, lora));
    }

    registerHook();
})();