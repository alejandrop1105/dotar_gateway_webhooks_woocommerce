// Funciones de utilidad para el dashboard

// ─── Clipboard ─────────────────────────────────────
window.clipboardHelper = {
    copyText: async function (text) {
        try {
            await navigator.clipboard.writeText(text);
            return true;
        } catch {
            var textarea = document.createElement('textarea');
            textarea.value = text;
            textarea.style.position = 'fixed';
            textarea.style.opacity = '0';
            document.body.appendChild(textarea);
            textarea.select();
            document.execCommand('copy');
            document.body.removeChild(textarea);
            return true;
        }
    }
};

// ─── Sidebar Toggle ────────────────────────────────
(function() {
    var floatingBtn = null;

    function createFloatingBtn() {
        if (floatingBtn) return;
        floatingBtn = document.createElement('button');
        floatingBtn.className = 'sidebar-floating-open';
        floatingBtn.title = 'Mostrar menú';
        floatingBtn.innerHTML = '☰';
        floatingBtn.addEventListener('click', function() { toggleSidebar(); });
        document.body.appendChild(floatingBtn);
    }

    function removeFloatingBtn() {
        if (floatingBtn) {
            floatingBtn.remove();
            floatingBtn = null;
        }
    }

    function toggleSidebar() {
        var sidebar = document.querySelector('.sidebar');
        var main = document.querySelector('.main-content');
        if (!sidebar || !main) return;

        var isHidden = sidebar.getAttribute('data-collapsed') === 'true';
        if (isHidden) {
            // Mostrar
            sidebar.style.transform = '';
            sidebar.setAttribute('data-collapsed', 'false');
            main.style.marginLeft = '';
            removeFloatingBtn();
        } else {
            // Ocultar
            sidebar.style.transform = 'translateX(-100%)';
            sidebar.setAttribute('data-collapsed', 'true');
            main.style.marginLeft = '0';
            createFloatingBtn();
        }
    }

    window.sidebarHelper = { toggle: toggleSidebar };
})();
