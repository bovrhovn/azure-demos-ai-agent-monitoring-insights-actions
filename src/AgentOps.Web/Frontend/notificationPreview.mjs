export function notificationPreview(value) {
    let text = "";
    let length = 0;
    for (const character of value ?? "") {
        if (length === 250) return { text, truncated: true };
        text += character;
        length++;
    }
    return { text, truncated: false };
}
