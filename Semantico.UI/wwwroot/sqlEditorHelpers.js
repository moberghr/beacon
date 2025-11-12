// SQL Editor Helpers for context-aware autocomplete
// Works with BlazorMonaco and standard Monaco editor

window.semanticoSqlEditor = {
    // Store completion disposables by editor ID
    completionDisposables: {},

    // Register context-aware SQL completion provider
    registerSqlCompletionProvider: function(editorId, sqlDialect, metadata) {
        try {
            console.log('[SQL Autocomplete] Registering completion provider');
            console.log('[SQL Autocomplete] Editor ID:', editorId);
            console.log('[SQL Autocomplete] SQL Dialect:', sqlDialect);
            console.log('[SQL Autocomplete] Tables available:', metadata?.tables?.length || 0);

            // Dispose of any existing completion provider for this editor
            if (this.completionDisposables[editorId]) {
                this.completionDisposables[editorId].dispose();
                delete this.completionDisposables[editorId];
            }

            if (!metadata || !metadata.tables) {
                console.warn('[SQL Autocomplete] No metadata provided for SQL completion');
                return false;
            }

            if (!window.monaco) {
                console.error('[SQL Autocomplete] Monaco editor not found!');
                return false;
            }

            // Register the completion provider with Monaco
            const disposable = monaco.languages.registerCompletionItemProvider('sql', {
                provideCompletionItems: (model, position) => {
                    try {
                        // Get text before cursor
                        const textUntilPosition = model.getValueInRange({
                            startLineNumber: 1,
                            startColumn: 1,
                            endLineNumber: position.lineNumber,
                            endColumn: position.column
                        });

                        // Get the word being typed
                        const word = model.getWordUntilPosition(position);
                        const range = {
                            startLineNumber: position.lineNumber,
                            endLineNumber: position.lineNumber,
                            startColumn: word.startColumn,
                            endColumn: word.endColumn
                        };

                        // Analyze context to determine what to suggest
                        const context = window.semanticoSqlEditor.analyzeContext(textUntilPosition);
                        console.log('[SQL Autocomplete] Context:', {
                            expectsTable: context.expectsTable,
                            expectsColumn: context.expectsColumn,
                            tableContext: context.tableContext
                        });

                        const suggestions = [];

                        // Add SQL keywords (always available but lower priority)
                        const keywords = [
                        'SELECT', 'FROM', 'WHERE', 'JOIN', 'INNER', 'LEFT', 'RIGHT', 'OUTER', 'ON',
                        'GROUP', 'BY', 'HAVING', 'ORDER', 'ASC', 'DESC', 'LIMIT', 'OFFSET',
                        'INSERT', 'INTO', 'VALUES', 'UPDATE', 'SET', 'DELETE', 'CREATE', 'TABLE',
                        'ALTER', 'DROP', 'INDEX', 'PRIMARY', 'KEY', 'FOREIGN', 'REFERENCES',
                        'AND', 'OR', 'NOT', 'NULL', 'IS', 'IN', 'LIKE', 'BETWEEN', 'EXISTS',
                        'CASE', 'WHEN', 'THEN', 'ELSE', 'END', 'AS', 'DISTINCT', 'COUNT',
                        'SUM', 'AVG', 'MAX', 'MIN', 'CAST', 'COALESCE', 'UNION', 'ALL'
                    ];

                    keywords.forEach(keyword => {
                        suggestions.push({
                            label: keyword,
                            kind: monaco.languages.CompletionItemKind.Keyword,
                            insertText: keyword,
                            detail: 'SQL Keyword',
                            sortText: '3_' + keyword, // Lower priority
                            range: range
                        });
                    });

                    // Add context-aware suggestions based on what we're expecting
                    if (context.expectsTable) {
                        // Suggest table names
                        metadata.tables.forEach(table => {
                            suggestions.push({
                                label: table.tableName,
                                kind: monaco.languages.CompletionItemKind.Class,
                                insertText: table.tableName,
                                detail: `Table in ${table.schemaName}`,
                                documentation: table.description || `${table.schemaName}.${table.tableName}`,
                                sortText: '0_' + table.tableName, // Highest priority
                                range: range
                            });
                        });
                    }

                    if (context.expectsColumn) {
                        if (context.tableContext) {
                            // Suggest columns from specific table
                            const table = metadata.tables.find(t =>
                                t.tableName.toLowerCase() === context.tableContext.toLowerCase()
                            );

                            if (table) {
                                table.columns.forEach(column => {
                                    const docs = [];
                                    docs.push(`Type: ${column.dataType}${column.isNullable ? ' (nullable)' : ''}`);
                                    if (column.isPrimaryKey) docs.push('Primary Key');
                                    if (column.isForeignKey) {
                                        docs.push(`Foreign Key → ${column.foreignKeyTable}.${column.foreignKeyColumn}`);
                                    }

                                    suggestions.push({
                                        label: column.columnName,
                                        kind: monaco.languages.CompletionItemKind.Field,
                                        insertText: column.columnName,
                                        detail: column.dataType,
                                        documentation: docs.join(' | '),
                                        sortText: '0_' + column.columnName, // Highest priority
                                        range: range
                                    });
                                });
                            }
                        } else {
                            // Suggest all columns with table prefix
                            metadata.tables.forEach(table => {
                                table.columns.forEach(column => {
                                    const docs = [];
                                    docs.push(`Table: ${table.tableName}`);
                                    docs.push(`Type: ${column.dataType}${column.isNullable ? ' (nullable)' : ''}`);
                                    if (column.isPrimaryKey) docs.push('Primary Key');
                                    if (column.isForeignKey) {
                                        docs.push(`FK → ${column.foreignKeyTable}.${column.foreignKeyColumn}`);
                                    }

                                    suggestions.push({
                                        label: `${table.tableName}.${column.columnName}`,
                                        kind: monaco.languages.CompletionItemKind.Field,
                                        insertText: column.columnName,
                                        detail: column.dataType,
                                        documentation: docs.join(' | '),
                                        sortText: '1_' + table.tableName + '_' + column.columnName,
                                        range: range
                                    });
                                });
                            });
                        }
                    }

                        return { suggestions: suggestions };
                    } catch (error) {
                        console.error('[SQL Autocomplete] Error in provideCompletionItems:', error);
                        return { suggestions: [] };
                    }
                }
            });

            this.completionDisposables[editorId] = disposable;
            console.log('[SQL Autocomplete] Provider registered successfully');
            return true;

        } catch (error) {
            console.error('[SQL Autocomplete] Error registering completion provider:', error);
            return false;
        }
    },

    // Analyze SQL context to determine what suggestions are appropriate
    analyzeContext: function(textBeforeCursor) {
        const upperText = textBeforeCursor.toUpperCase().trim();

        // Remove comments and strings for better parsing
        const cleanText = upperText
            .replace(/--[^\n]*/g, '') // Remove single-line comments
            .replace(/'[^']*'/g, "''"); // Remove string literals

        const tokens = cleanText.split(/\s+/).filter(t => t.length > 0);
        const lastToken = tokens[tokens.length - 1] || '';
        const secondLastToken = tokens[tokens.length - 2] || '';

        const context = {
            expectsTable: false,
            expectsColumn: false,
            tableContext: null
        };

        // Check for table.column pattern
        const dotMatch = textBeforeCursor.match(/(\w+)\.$/);
        if (dotMatch) {
            context.expectsColumn = true;
            context.tableContext = dotMatch[1];
            return context;
        }

        // Check if we expect a table name (after FROM, JOIN, INTO, UPDATE, TABLE)
        if (lastToken === 'FROM' || lastToken === 'JOIN' ||
            lastToken === 'INTO' || lastToken === 'TABLE' ||
            (lastToken === 'UPDATE' && tokens.length === 1)) {
            context.expectsTable = true;
            context.expectsColumn = false;
            return context;
        }

        // After JOIN types, we expect a table
        if ((lastToken === 'INNER' || lastToken === 'LEFT' ||
             lastToken === 'RIGHT' || lastToken === 'OUTER' ||
             lastToken === 'CROSS' || lastToken === 'FULL') &&
            secondLastToken === 'JOIN') {
            context.expectsTable = true;
            context.expectsColumn = false;
            return context;
        }

        // Check if we expect columns (after SELECT, WHERE, ON, SET, etc.)
        if (lastToken === 'SELECT' || lastToken === 'WHERE' ||
            lastToken === 'AND' || lastToken === 'OR' ||
            lastToken === 'ON' || lastToken === 'SET' ||
            lastToken === 'BY' || lastToken === 'HAVING') {
            context.expectsColumn = true;
            return context;
        }

        // After comma in SELECT or WHERE context
        if (lastToken === ',') {
            // Check if we're in SELECT context (before FROM) or WHERE context
            const fromIndex = cleanText.lastIndexOf('FROM');
            const selectIndex = cleanText.lastIndexOf('SELECT');
            const whereIndex = cleanText.lastIndexOf('WHERE');

            if ((selectIndex > fromIndex && fromIndex !== -1) ||
                whereIndex > Math.max(selectIndex, fromIndex)) {
                context.expectsColumn = true;
                return context;
            }
        }

        // In SELECT clause (between SELECT and FROM)
        if (cleanText.includes('SELECT')) {
            const lastFrom = cleanText.lastIndexOf('FROM');
            const lastSelect = cleanText.lastIndexOf('SELECT');

            if (lastSelect > lastFrom || lastFrom === -1) {
                // We're in the SELECT column list
                context.expectsColumn = true;
                return context;
            }
        }

        // Default: show both tables and columns
        context.expectsColumn = true;
        context.expectsTable = true;
        return context;
    },

    // Dispose of completion provider
    disposeCompletionProvider: function(editorId) {
        if (this.completionDisposables[editorId]) {
            this.completionDisposables[editorId].dispose();
            delete this.completionDisposables[editorId];
            return true;
        }
        return false;
    },

    // Set drag data for HTML5 drag/drop
    setDragData: function(event, dragData) {
        try {
            event.dataTransfer.effectAllowed = 'copy';
            event.dataTransfer.setData('text/plain', dragData);
            console.log('[Drag/Drop] Drag started with data:', dragData);
            return true;
        } catch (error) {
            console.error('[Drag/Drop] Error setting drag data:', error);
            return false;
        }
    },

    // Store editor instances for drop handling
    editorInstances: {},

    // Store editor instance for later use
    storeEditorInstance: function(editorId, editor) {
        this.editorInstances[editorId] = editor;
        console.log('[Drag/Drop] Stored editor instance for:', editorId);
    },

    // Enable drop on Monaco editor
    enableDropOnEditor: function(editorId) {
        try {
            // Find the Monaco editor DOM element
            const editorElement = document.getElementById(editorId);
            if (!editorElement) {
                console.error('[Drag/Drop] Editor element not found:', editorId);
                return false;
            }

            // Find the actual editor content area
            const editorContent = editorElement.querySelector('.monaco-editor');
            if (!editorContent) {
                console.error('[Drag/Drop] Monaco editor content not found');
                return false;
            }

            // Get Monaco editor instance - try multiple approaches
            let editorInstance = this.editorInstances[editorId];

            // If not stored, try to get it from the global monaco object
            if (!editorInstance && window.monaco && window.monaco.editor) {
                const allEditors = window.monaco.editor.getEditors();
                if (allEditors && allEditors.length > 0) {
                    // Use the first editor (assuming single editor on page)
                    editorInstance = allEditors[0];
                    this.editorInstances[editorId] = editorInstance;
                    console.log('[Drag/Drop] Found editor instance from monaco.editor.getEditors()');
                }
            }

            if (!editorInstance) {
                console.error('[Drag/Drop] Could not find editor instance. Will use fallback insertion method.');
            }

            // Allow drop by preventing default on dragover
            editorContent.addEventListener('dragover', function(e) {
                e.preventDefault();
                e.stopPropagation();
                e.dataTransfer.dropEffect = 'copy';
            });

            // Handle the drop event
            const self = this;
            editorContent.addEventListener('drop', function(e) {
                e.preventDefault();
                e.stopPropagation();

                const dragData = e.dataTransfer.getData('text/plain');
                if (!dragData) {
                    console.warn('[Drag/Drop] No drag data found');
                    return;
                }

                console.log('[Drag/Drop] Drop received with data:', dragData);

                // Get latest editor instance reference
                let editor = self.editorInstances[editorId];
                if (!editor && window.monaco && window.monaco.editor) {
                    const allEditors = window.monaco.editor.getEditors();
                    if (allEditors && allEditors.length > 0) {
                        editor = allEditors[0];
                    }
                }

                if (!editor) {
                    console.error('[Drag/Drop] Editor instance not available');
                    return;
                }

                try {
                    // Get current cursor position
                    const position = editor.getPosition();
                    if (!position) {
                        console.error('[Drag/Drop] Could not get editor position');
                        return;
                    }

                    // Insert the text at cursor position
                    editor.executeEdits('drag-drop', [{
                        range: {
                            startLineNumber: position.lineNumber,
                            startColumn: position.column,
                            endLineNumber: position.lineNumber,
                            endColumn: position.column
                        },
                        text: dragData
                    }]);

                    // Move cursor to end of inserted text
                    const lines = dragData.split('\n');
                    const lastLine = lines[lines.length - 1];
                    const newPosition = {
                        lineNumber: position.lineNumber + lines.length - 1,
                        column: lines.length > 1 ? lastLine.length + 1 : position.column + dragData.length
                    };
                    editor.setPosition(newPosition);
                    editor.focus();

                    console.log('[Drag/Drop] Text inserted successfully');
                } catch (error) {
                    console.error('[Drag/Drop] Error inserting text:', error);
                }
            });

            console.log('[Drag/Drop] Drop handlers enabled for editor:', editorId);
            return true;
        } catch (error) {
            console.error('[Drag/Drop] Error enabling drop:', error);
            return false;
        }
    }
};

console.log('[SQL Autocomplete] Helpers loaded successfully');
