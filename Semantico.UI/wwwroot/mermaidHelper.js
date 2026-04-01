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

    // Render each diagram individually so one broken diagram doesn't block others
    for (const block of blocks) {
        try {
            await window.mermaid.run({ nodes: [block] });
        } catch (e) {
            console.error('Mermaid rendering failed for diagram:', e);
            const originalSource = block.textContent;
            block.classList.add('mermaid-error');
            // Clear and rebuild using safe DOM methods
            while (block.firstChild) block.removeChild(block.firstChild);
            const wrapper = document.createElement('div');
            wrapper.style.cssText = 'color: #e57373; padding: 1rem; border: 1px solid #e57373; border-radius: 4px; font-family: monospace; font-size: 0.85rem;';
            const label = document.createElement('strong');
            label.textContent = 'Diagram rendering failed';
            wrapper.appendChild(label);
            const source = document.createElement('pre');
            source.style.cssText = 'white-space: pre-wrap; margin-top: 0.5rem; color: #aaa;';
            source.textContent = originalSource;
            wrapper.appendChild(source);
            block.appendChild(wrapper);
        }
    }
};
