function loadComponent(id, file, callback) {
    fetch(file)
        .then(res => {
            if (!res.ok) throw new Error(`Failed to load ${file}: ${res.status}`);
            return res.text();
        })
        .then(data => {
            document.getElementById(id).innerHTML = data;
            if (callback) callback(); // run callback after content is loaded
        })
        .catch(err => console.error(err));
}
// Automatically load sidebar and topbar
// document.addEventListener("DOMContentLoaded", () => {
//     loadComponent("sidebar", "components/sidebar.html");
//     loadComponent("topbar", "components/topbar.html");
// });


// Automatically load sidebar and topbar
document.addEventListener("DOMContentLoaded", () => {
    console.log("DOM fully loaded and parsed. Loading components...");

    // Load sidebar and then set active link
    loadComponent("sidebar", "components/sidebar.html", () => {
        console.log("Sidebar loaded. Setting active link...");

        const navLinks = document.querySelectorAll(".sidebar .nav-link");
        const currentPage = window.location.pathname.split("/").pop();
        console.log("Current page:", currentPage);

        navLinks.forEach(link => {
            if (link.getAttribute("href") === currentPage) {
                link.classList.add("active");
            } else {
                link.classList.remove("active");
            }
        }); 
    });

    // Load topbar
    loadComponent("topbar", "components/topbar.html");
}); 