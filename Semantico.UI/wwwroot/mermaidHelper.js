window.renderMermaidDiagrams = async () => {
    // Wait for mermaid to be available (loaded as ESM module)
    for (let i = 0; i < 20; i++) {
        if (typeof window.mermaid !== 'undefined') break;
        await new Promise(r => setTimeout(r, 250));
    }
    if (typeof window.mermaid === 'undefined') {
        console.warn('Mermaid not available');
        return;
    }

    // Markdig renders ```mermaid blocks as <pre class="mermaid"> with HTML-encoded content
    const blocks = document.querySelectorAll('pre.mermaid');
    if (blocks.length === 0) return;

    for (const block of blocks) {
        // Decode HTML entities so mermaid can parse the syntax
        block.textContent = block.textContent;
    }

    try {
        await window.mermaid.run({ nodes: blocks });
    } catch (e) {
        console.error('Mermaid rendering failed:', e);
    }
};
