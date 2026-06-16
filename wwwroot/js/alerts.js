// Show toast message
function showToast(type, title, message) {
    const toastId = 'toast-' + Date.now();
    const toastHtml = `
                    <div id="${toastId}" class="toast" role="alert" aria-live="assertive" aria-atomic="true" data-bs-delay="5000">
                        <div class="toast-header bg-${type} text-white">
                            <strong class="me-auto">${title}</strong>
                            <button type="button" class="btn-close btn-close-white" data-bs-dismiss="toast" aria-label="Close"></button>
                        </div>
                        <div class="toast-body">
                            ${message}
                        </div>
                    </div>
                `;

    document.getElementById('toastContainer').insertAdjacentHTML('beforeend', toastHtml);

    const toastElement = document.getElementById(toastId);
    const toast = new bootstrap.Toast(toastElement);
    toast.show();

    toastElement.addEventListener('hidden.bs.toast', function () {
        this.remove();
    });
}