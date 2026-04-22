// Authentication helpers for Beacon login
// These functions run in the browser to properly handle cookies

async function beaconLogin(apiUrl, username, password, rememberMe) {
    try {
        const response = await fetch(apiUrl, {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json',
            },
            body: JSON.stringify({
                username: username,
                password: password,
                rememberMe: rememberMe
            }),
            credentials: 'same-origin' // Important: include cookies
        });

        if (response.ok) {
            const data = await response.json();
            return {
                success: data.success,
                error: data.error,
                redirectUrl: data.redirectUrl
            };
        } else {
            return {
                success: false,
                error: 'An error occurred during login.',
                redirectUrl: null
            };
        }
    } catch (error) {
        console.error('Login error:', error);
        return {
            success: false,
            error: 'An error occurred during login.',
            redirectUrl: null
        };
    }
}

async function beaconLogout(apiUrl) {
    try {
        await fetch(apiUrl, {
            method: 'POST',
            credentials: 'same-origin'
        });
        return true;
    } catch (error) {
        console.error('Logout error:', error);
        return false;
    }
}
