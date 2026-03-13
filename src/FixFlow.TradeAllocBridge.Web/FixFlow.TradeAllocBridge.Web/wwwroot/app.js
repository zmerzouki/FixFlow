window.fixflowDownloadFile = (fileName, contentType, content) => {
    const blob = new Blob([content], { type: contentType });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = fileName;
    anchor.style.display = 'none';
    document.body.appendChild(anchor);
    anchor.click();
    document.body.removeChild(anchor);
    URL.revokeObjectURL(url);
};

window.fixflowScrollToId = (id) => {
    if (!id) {
        return;
    }

    const target = document.getElementById(id);
    if (!target) {
        return;
    }

    target.scrollIntoView({ behavior: 'smooth', block: 'start' });
};

window.fixflowTheme = (() => {
    const storageKey = "fixflow-theme";

    const apply = (mode) => {
        const root = document.documentElement;
        if (mode === "dark") {
            root.classList.add("theme-dark");
        } else {
            root.classList.remove("theme-dark");
        }
    };

    const init = () => {
        try {
            const stored = localStorage.getItem(storageKey);
            if (stored === "dark" || stored === "light") {
                apply(stored);
                return;
            }
        } catch {
            // Ignore storage errors.
        }

        if (window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches) {
            apply("dark");
        }
    };

    const set = (isDark) => {
        const mode = isDark ? "dark" : "light";
        apply(mode);
        try {
            localStorage.setItem(storageKey, mode);
        } catch {
            // Ignore storage errors.
        }
        return isDark;
    };

    const toggle = () => {
        const root = document.documentElement;
        const isDark = !root.classList.contains("theme-dark");
        return set(isDark);
    };

    const get = () => document.documentElement.classList.contains("theme-dark");

    init();

    return { init, set, toggle, get };
})();

window.fixflowApplyAssetVersion = (() => {
    const getVersion = () => {
        const meta = document.querySelector("meta[name='fixflow-asset-version']");
        return meta?.getAttribute("content") || "";
    };

    const shouldSkip = (url) => {
        if (!url) return true;
        if (url.startsWith("data:") || url.startsWith("blob:") || url.startsWith("#")) return true;
        return false;
    };

    const isSameOrigin = (url) => {
        try {
            const resolved = new URL(url, window.location.origin);
            return resolved.origin === window.location.origin;
        } catch {
            return false;
        }
    };

    const appendVersion = (url, version) => {
        const resolved = new URL(url, window.location.origin);
        if (resolved.searchParams.has("v")) {
            return url;
        }
        if (resolved.pathname.startsWith("/_framework/")) {
            return url;
        }
        resolved.searchParams.set("v", version);
        return resolved.pathname + resolved.search + resolved.hash;
    };

    const apply = () => {
        const version = getVersion();
        if (!version) return;

        const assets = [
            ...document.querySelectorAll("img[src]"),
            ...document.querySelectorAll("link[rel='stylesheet'][href]"),
            ...document.querySelectorAll("script[src]")
        ];

        assets.forEach((el) => {
            const attr = el.tagName === "LINK" ? "href" : "src";
            const url = el.getAttribute(attr);
            if (shouldSkip(url)) return;
            if (!isSameOrigin(url)) return;
            const updated = appendVersion(url, version);
            if (updated !== url) {
                el.setAttribute(attr, updated);
            }
        });
    };

    if (document.readyState === "loading") {
        document.addEventListener("DOMContentLoaded", apply);
    } else {
        apply();
    }

    return { apply };
})();

window.fixflowTagSuggest = (() => {
    let dotNetRef = null;
    const capture = true;

    const isHandledKey = (key) =>
        key === "ArrowDown" || key === "ArrowUp" || key === "Enter" || key === "Escape";

    const isTagInput = (element) =>
        element && element.getAttribute("data-fixflow-tag-input") === "true";

    const getTagInput = (event) => {
        const target = event?.target;
        if (isTagInput(target)) {
            return target;
        }

        const path = event?.composedPath?.();
        if (path && path.length) {
            for (const node of path) {
                if (isTagInput(node)) {
                    return node;
                }
            }
        }

        return document.activeElement && isTagInput(document.activeElement)
            ? document.activeElement
            : null;
    };

    const isSuggestOpen = (element) => {
        if (!element) {
            return false;
        }

        const container = element.closest(".fix-suggest");
        if (!container) {
            return false;
        }

        return !!container.querySelector(".fix-suggest-list");
    };

    const handler = (event) => {
        const key = event?.key;
        if (!isHandledKey(key)) {
            return;
        }

        const tagInput = getTagInput(event);
        const open = tagInput ? isSuggestOpen(tagInput) : false;

        if (!tagInput || !open || !dotNetRef) {
            return;
        }

        event.preventDefault();
        event.stopPropagation();
        event.stopImmediatePropagation();
        dotNetRef.invokeMethodAsync("HandleTagSuggestKeyAsync", key);
    };

    const updatePosition = () => {
        const lists = document.querySelectorAll(".fix-suggest-list");
        lists.forEach((list) => {
            const container = list.closest(".fix-suggest");
            if (!container) {
                return;
            }

            container.classList.remove("is-flipped");
            const containerRect = container.getBoundingClientRect();
            const listHeight = list.offsetHeight;
            const spaceAbove = containerRect.top;
            const spaceBelow = window.innerHeight - containerRect.bottom;

            if (spaceBelow < listHeight && spaceAbove >= listHeight) {
                container.classList.add("is-flipped");
            }
        });
    };

    const ensureActiveVisible = () => {
        const lists = document.querySelectorAll(".fix-suggest-list");
        lists.forEach((list) => {
            const active = list.querySelector(".fix-suggest-item.is-active");
            if (!active) {
                return;
            }

            const viewTop = list.scrollTop;
            const viewBottom = viewTop + list.clientHeight;
            const itemTop = active.offsetTop;
            const itemBottom = itemTop + active.offsetHeight;

            if (itemTop < viewTop) {
                list.scrollTop = itemTop;
            } else if (itemBottom > viewBottom) {
                list.scrollTop = itemBottom - list.clientHeight;
            }
        });
    };

    const register = (ref) => {
        dotNetRef = ref || null;
        if (!handler._bound) {
            document.addEventListener("keydown", handler, capture);
            window.addEventListener("resize", updatePosition);
            handler._bound = true;
        }
    };

    const unregister = () => {
        dotNetRef = null;
        if (handler._bound) {
            document.removeEventListener("keydown", handler, capture);
            window.removeEventListener("resize", updatePosition);
            handler._bound = false;
        }
    };

    return { register, unregister, updatePosition, ensureActiveVisible };
})();
