(function () {

    const scripts = [
        "/js/api-service.js",
        "/js/session-manager.js",
        "/js/conversion.js",
        "/js/alerts.js",
    ];

    function loadScripts(index = 0) {

        if (index >= scripts.length) return;

        const script = document.createElement("script");
        script.src = scripts[index];
        script.defer = true;

        script.onload = function () {
            loadScripts(index + 1);
        };

        script.onerror = function () {
            console.error("Failed to load script:", scripts[index]);
        };

        document.head.appendChild(script);
    }

    loadScripts();

    function loadSidebar(){
        // Load sidebar HTML
        $("#sidebarContainer").load("/sidebar.html");

        // Toggle sidebar
        $(document).on("click", "#btnToggleSidebar", function () {
            $("#sidebar").toggleClass("open");
            $("#sidebarOverlay").toggleClass("active");
        });

        // Close on overlay click
        $("#sidebarOverlay").on("click", function () {
            $("#sidebar").removeClass("open");
            $(this).removeClass("active");
        });

        $(document).on("click", ".sidebar-menu a", function () {
            $("#sidebar").removeClass("open");
            $("#sidebarOverlay").removeClass("active");
        });

        let currentPage = window.location.pathname;

        $('.sidebar-menu a').each(function () {
            if (this.getAttribute("href") === currentPage) {
                $(this).css("background", "rgba(255,255,255,0.2)");
            }
        });
    }

    loadSidebar();

})();
