// Session Management
function saveSession(data) {
    localStorage.setItem('pulse_token', data.token);
    localStorage.setItem('pulse_userId', data.userId);
    localStorage.setItem('pulse_roleId', data.roleId);
    localStorage.setItem('pulse_userName', data.userName);
    localStorage.setItem('pulse_lastLogin', new Date().toISOString());

    // Set token expiry
    const expiry = new Date();
    expiry.setHours(expiry.getHours() + 8);
    localStorage.setItem('pulse_tokenExpiry', expiry.toISOString());
}
function clearSession() {
    localStorage.removeItem('pulse_token');
    localStorage.removeItem('pulse_userId');
    localStorage.removeItem('pulse_roleId');
    localStorage.removeItem('pulse_userName');
    localStorage.removeItem('pulse_lastLogin');
    localStorage.removeItem('pulse_tokenExpiry');
    window.location.href = 'login.html';
}