export function timestamp(value) {
    return value ? new Date(value).toISOString().replace("T", " ").slice(0, 19) : "";
}

export async function request(url, params = {}, signal) {
    const target = new URL(url, window.location.origin);
    Object.entries(params).forEach(([key, value]) => target.searchParams.set(key, value));
    const response = await fetch(target, {
        headers: { Accept: "application/json" }, credentials: "same-origin", redirect: "error", signal
    });
    if (!response.ok) {
        if (response.status === 503) {
            const problem = await response.json();
            if (problem.title === "Telemetry is not configured.") throw new Error(problem.detail);
        }
        if (response.status === 401 || response.status === 403)
            throw new Error("Your session has expired or access was denied. Reload this page to sign in.");
        throw new Error(`The data could not be loaded (HTTP ${response.status}). Please retry. See application logs for details.`);
    }
    return response.json();
}
