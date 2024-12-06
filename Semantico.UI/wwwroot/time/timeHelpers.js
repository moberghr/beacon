// time helpers


window.toLocalTime = (utcDateTime) => {
    if (!utcDateTime) return null; // Handle null or undefined input
    const date = new Date(utcDateTime);
    return date.toLocaleString(); // Adjusts to client's local time
};