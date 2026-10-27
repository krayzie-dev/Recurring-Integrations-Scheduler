// RIS 5 UI helpers: preferences cookie, file download, export of SVG charts to PNG, printing.
window.ris = {
    setCookie: (name, value, days) => {
        const expires = new Date(Date.now() + days * 864e5).toUTCString();
        document.cookie = `${name}=${encodeURIComponent(value)}; expires=${expires}; path=/; SameSite=Lax`;
    },

    downloadFile: async (fileName, contentType, streamReference) => {
        const buffer = await streamReference.arrayBuffer();
        ris.downloadBlob(new Blob([buffer], { type: contentType }), fileName);
    },

    downloadBlob: (blob, fileName) => {
        const a = document.createElement("a");
        a.href = URL.createObjectURL(blob);
        a.download = fileName;
        a.click();
        setTimeout(() => URL.revokeObjectURL(a.href), 1000);
    },

    // Renders the SVG chart to a PNG data URL: computed styles are copied into a clone (theme colours come from CSS
    // variables, which a standalone image does not have), then drawn on a canvas at 2x scale over the page background.
    renderSvgPng: (elementId) => new Promise((resolve, reject) => {
        const svg = document.getElementById(elementId);
        if (!svg) { reject(new Error(`Chart ${elementId} not found`)); return; }
        const clone = svg.cloneNode(true);
        const source = svg.querySelectorAll("*");
        const target = clone.querySelectorAll("*");
        const properties = ["fill", "fill-opacity", "stroke", "stroke-width", "stroke-dasharray", "opacity",
            "font-size", "font-family", "font-weight", "text-anchor", "dominant-baseline", "display"];
        source.forEach((node, i) => {
            const style = getComputedStyle(node);
            target[i].setAttribute("style", properties.map(p => `${p}:${style.getPropertyValue(p)}`).join(";"));
        });
        const width = Math.ceil(svg.getBoundingClientRect().width);
        const height = Math.ceil(svg.getBoundingClientRect().height);
        clone.setAttribute("width", width);
        clone.setAttribute("height", height);
        const background = ris.backgroundOf(svg);
        const image = new Image();
        image.onerror = () => reject(new Error("SVG could not be rendered"));
        image.onload = () => {
            const scale = 2;
            const canvas = document.createElement("canvas");
            canvas.width = width * scale;
            canvas.height = height * scale;
            const ctx = canvas.getContext("2d");
            ctx.fillStyle = background;
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            ctx.scale(scale, scale);
            ctx.drawImage(image, 0, 0);
            resolve(canvas.toDataURL("image/png"));
        };
        image.src = "data:image/svg+xml;charset=utf-8," + encodeURIComponent(new XMLSerializer().serializeToString(clone));
    }),

    // First non-transparent background of the element or its ancestors; the theme background otherwise.
    backgroundOf: (element) => {
        for (let node = element; node && node.nodeType === 1; node = node.parentElement) {
            const colour = getComputedStyle(node).backgroundColor;
            if (colour && colour !== "transparent" && !/rgba\(.*,\s*0\)$/.test(colour)) return colour;
        }
        return getComputedStyle(document.documentElement).getPropertyValue("--mud-palette-background").trim() || "#ffffff";
    },

    exportSvgToPng: async (elementId, fileName) => {
        const dataUrl = await ris.renderSvgPng(elementId);
        const blob = await (await fetch(dataUrl)).blob();
        ris.downloadBlob(blob, fileName);
    },

    print: () => window.print()
};
