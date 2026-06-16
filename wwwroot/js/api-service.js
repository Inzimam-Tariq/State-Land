// apiService.js

async function executeSP(procName, parameters = {}, isPublic = false, timeoutMs = 80000) {
    const authToken = localStorage.getItem("pulse_token");
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), timeoutMs);
    let endpoint = "./api/Gis/ExecuteStoredProcedure";
    if (isPublic)
        endpoint = "./api/Gis/ExecuteStoredProcedureAnonymous";

    try {
        //debugger
        const res = await fetch(endpoint, {
            method: "POST",
            headers: {
                "Content-Type": "application/json",
                ...(authToken && { "Authorization": `Bearer ${authToken}` })
            },
            body: JSON.stringify({ procedureName: procName, parameters }),
            signal: controller.signal
        });

        clearTimeout(timeout);
        //debugger
        // Read body ONCE
        const rawText = await res.text();

        let parsedData = null;

        if (rawText) {
            try {
                parsedData = JSON.parse(rawText);
            } catch {
                parsedData = rawText;
            }
        }

        // Handle HTTP errors
        if (!res.ok) {
            throw new Error(
                parsedData?.Message ||
                parsedData?.Error ||
                parsedData ||
                `HTTP ${res.status} - Server error`
            );
        }

        // Handle empty success
        if (!parsedData) {
            return [];
        }

        return normalizeResponse(parsedData);

    } catch (err) {

        clearTimeout(timeout);

        let message;

        if (err.name === "AbortError") {
            message = "Request timeout. Server took too long to respond.";
        }
        else if (err.message) {
            message = err.message;
        }
        else {
            message = "Network or unexpected system error.";
        }

        console.error(`API call failed (${procName}):`, message);

        if (window.Swal) {
            Swal.fire("Error", `Failed to execute ${procName}: ${message}`, "error");
        }

        return [];
    }
}

function normalizeResponse(data) {

    if (!data) return [];

    let resultSets = [];

    // Multi-result format
    if (Array.isArray(data) &&
        data.length > 0 &&
        (data[0].resultSetIndex !== undefined || data[0].ResultSetIndex !== undefined)) {

        resultSets = data;
    }

    // Flat array (single result)
    else if (Array.isArray(data)) {
        resultSets = [{ ResultSetIndex: 0, Rows: data }];
    }

    // Wrapped response
    else if (Array.isArray(data.response)) {
        resultSets = data.response;
    }

    // Single object with rows
    else if (Array.isArray(data.rows) || Array.isArray(data.Rows)) {
        resultSets = [{
            ResultSetIndex: 0,
            Rows: data.rows || data.Rows
        }];
    }

    // Unexpected object but not empty
    else if (typeof data === "object") {
        console.warn("Unexpected but valid object response:", data);
        resultSets = [{
            ResultSetIndex: 0,
            Rows: [data]
        }];
    }

    else {
        console.warn("Unhandled API response format:", data);
        return [];
    }

    return resultSets.map(rs => ({
        index: rs.resultSetIndex ?? rs.ResultSetIndex ?? 0,
        rows: rs.rows ?? rs.Rows ?? []
    }));
}
