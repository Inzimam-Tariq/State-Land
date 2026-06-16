let view;

require([
    "esri/Map",
    "esri/views/MapView"
], function (Map, MapView) {

    const map = new Map({
        basemap: "topo-vector"
    });

    view = new MapView({
        container: "viewDiv",
        map: map,
        center: [72.8, 30.5],
        zoom: 6
    });

    loadDistricts();
});

function loadDistricts() {
    fetch('/api/location/districts')
        .then(r => r.json())
        .then(data => {
            data.forEach(d => {
                ddlDistrict.innerHTML += `<option value="${d.id}">${d.name}</option>`;
            });
        });
}

ddlDistrict.onchange = function () {
    ddlTehsil.innerHTML = `<option>Select Tehsil</option>`;
    ddlMouza.innerHTML = `<option>Select Mouza</option>`;

    fetch(`/api/location/tehsils?districtId=${this.value}`)
        .then(r => r.json())
        .then(data => {
            data.forEach(t => {
                ddlTehsil.innerHTML += `<option value="${t.id}">${t.name}</option>`;
            });
        });
};

ddlTehsil.onchange = function () {
    ddlMouza.innerHTML = `<option>Select Mouza</option>`;

    fetch(`/api/location/mouzas?tehsilId=${this.value}`)
        .then(r => r.json())
        .then(data => {
            data.forEach(m => {
                ddlMouza.innerHTML += `<option value="${m.id}">${m.name}</option>`;
            });
        });
};
