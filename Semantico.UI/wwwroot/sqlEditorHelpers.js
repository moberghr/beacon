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
                triggerCharacters: ['.', ' '],
                provideCompletionItems: (model, position) => {
                    try {
                        // Validate that this model belongs to the target editor
                        let editor = window.semanticoSqlEditor.editorInstances[editorId];
                        
                        // Attempt to find editor if not stored
                        if (!editor && window.monaco && window.monaco.editor) {
                             const editors = window.monaco.editor.getEditors();
                             editor = editors.find(e => {
                                 const domNode = e.getContainerDomNode();
                                 return domNode && domNode.id === editorId;
                             });
                             if (editor) {
                                 window.semanticoSqlEditor.editorInstances[editorId] = editor;
                             }
                        }

                        // If we found an editor and the model doesn't match, this provider shouldn't return results
                        if (!editor || editor.getModel() !== model) {
                            return { suggestions: [] };
                        }

                        // Get text up to cursor
                        const textUntilPosition = model.getValueInRange({
                            startLineNumber: 1,
                            startColumn: 1,
                            endLineNumber: position.lineNumber,
                            endColumn: position.column
                        });

                        // Get full text for alias analysis
                        const fullText = model.getValue();

                        // Analyze context
                        const context = window.semanticoSqlEditor.analyzeContext(textUntilPosition, fullText, metadata);
                        console.log('[SQL Autocomplete] Context:', context);

                        const suggestions = [];
                        const range = {
                            startLineNumber: position.lineNumber,
                            endLineNumber: position.lineNumber,
                            startColumn: position.column, // default to current pos
                            endColumn: position.column
                        };
                        
                        // Get word info for replacement range
                        const wordInfo = model.getWordUntilPosition(position);
                        if (wordInfo) {
                            range.startColumn = wordInfo.startColumn;
                            range.endColumn = wordInfo.endColumn;
                        }

                        // 1. Handle specific schema context (e.g. "schema.")
                        if (context.schemaContext) {
                            const schemaName = context.schemaContext.toLowerCase();
                            // Suggest tables in this schema
                            metadata.tables.forEach(table => {
                                if (table.schemaName.toLowerCase() === schemaName) {
                                    suggestions.push({
                                        label: table.tableName,
                                        kind: monaco.languages.CompletionItemKind.Class,
                                        insertText: table.tableName,
                                        detail: `Table in ${table.schemaName}`,
                                        documentation: table.description || `${table.schemaName}.${table.tableName}`,
                                        sortText: '0_' + table.tableName,
                                        range: range
                                    });
                                }
                            });
                            return { suggestions: suggestions };
                        }

                        // 2. Handle specific table/alias context (e.g. "t." or "mytable.")
                        if (context.tableOrAliasContext) {
                            const contextName = context.tableOrAliasContext.toLowerCase();
                            let targetTable = null;

                            // Check if it's a known alias
                            if (context.aliases[contextName]) {
                                const fullTableName = context.aliases[contextName]; // schema.table or just table
                                
                                // Find table by name (and optionally schema)
                                targetTable = metadata.tables.find(t => {
                                    if (fullTableName.includes('.')) {
                                        const [s, n] = fullTableName.split('.');
                                        return t.schemaName.toLowerCase() === s.toLowerCase() && 
                                               t.tableName.toLowerCase() === n.toLowerCase();
                                    } else {
                                        return t.tableName.toLowerCase() === fullTableName.toLowerCase();
                                    }
                                });
                            } 
                            // Check if it's a direct table name (without schema or with schema included in check)
                            else {
                                targetTable = metadata.tables.find(t => 
                                    t.tableName.toLowerCase() === contextName
                                );
                            }

                            if (targetTable) {
                                targetTable.columns.forEach(column => {
                                    const docs = [];
                                    docs.push(`Type: ${column.dataType}${column.isNullable ? ' (nullable)' : ''}`);
                                    if (column.isPrimaryKey) docs.push('Primary Key');
                                    if (column.isForeignKey) docs.push(`FK → ${column.foreignKeyTable}.${column.foreignKeyColumn}`);

                                    suggestions.push({
                                        label: column.columnName,
                                        kind: monaco.languages.CompletionItemKind.Field,
                                        insertText: column.columnName,
                                        detail: column.dataType,
                                        documentation: docs.join(' | '),
                                        sortText: '0_' + column.columnName,
                                        range: range
                                    });
                                });
                            }
                            return { suggestions: suggestions };
                        }

                        // 3. Handle expectation of a table (after FROM, JOIN, etc.)
                        if (context.expectsTable) {
                            // Suggest Schemas first
                            const schemas = [...new Set(metadata.tables.map(t => t.schemaName))];
                            schemas.forEach(schema => {
                                suggestions.push({
                                    label: schema,
                                    kind: monaco.languages.CompletionItemKind.Module,
                                    insertText: schema,
                                    detail: 'Schema',
                                    sortText: '0_' + schema, // High priority
                                    range: range
                                });
                            });

                            // Also suggest tables directly (but lower priority than schemas if they exist)
                            metadata.tables.forEach(table => {
                                suggestions.push({
                                    label: table.tableName,
                                    kind: monaco.languages.CompletionItemKind.Class,
                                    insertText: table.tableName,
                                    detail: `Table (${table.schemaName})`,
                                    documentation: table.description,
                                    sortText: '1_' + table.tableName,
                                    range: range
                                });
                            });
                        }

                        // 4. Handle expectation of a column (SELECT list, WHERE, etc.)
                        if (context.expectsColumn) {
                            // Suggest aliases defined in the query
                            Object.keys(context.aliases).forEach(alias => {
                                suggestions.push({
                                    label: alias,
                                    kind: monaco.languages.CompletionItemKind.Reference,
                                    insertText: alias,
                                    detail: `Alias for ${context.aliases[alias]}`,
                                    sortText: '0_' + alias,
                                    range: range
                                });
                            });

                            // Track unique column names to avoid duplicates
                            const seenColumns = new Set();

                            // Suggest all columns (qualified with table name for clarity)
                            metadata.tables.forEach(table => {
                                table.columns.forEach(column => {
                                    const uniqueKey = `${column.columnName.toLowerCase()}`;

                                    // Only add if we haven't seen this column name before
                                    if (!seenColumns.has(uniqueKey)) {
                                        seenColumns.add(uniqueKey);

                                        // Find all tables that have this column
                                        const tablesWithColumn = metadata.tables.filter(t =>
                                            t.columns.some(c => c.columnName.toLowerCase() === column.columnName.toLowerCase())
                                        );

                                        let detail = column.columnName;
                                        let documentation = `Type: ${column.dataType}`;

                                        // If column exists in multiple tables, show that info
                                        if (tablesWithColumn.length > 1) {
                                            const tableNames = tablesWithColumn.map(t => t.tableName).join(', ');
                                            detail = `${column.columnName} (in ${tablesWithColumn.length} tables)`;
                                            documentation = `Found in: ${tableNames}\nType: ${column.dataType}`;
                                        } else {
                                            detail = `${table.tableName}.${column.columnName}`;
                                            documentation = `Table: ${table.schemaName}.${table.tableName}\nType: ${column.dataType}`;
                                        }

                                        suggestions.push({
                                            label: column.columnName,
                                            kind: monaco.languages.CompletionItemKind.Field,
                                            insertText: column.columnName,
                                            detail: detail,
                                            documentation: documentation,
                                            sortText: '2_' + column.columnName,
                                            range: range
                                        });
                                    }
                                });
                            });
                        }

                        // Always add keywords if not in a specific dot context
                        if (!context.schemaContext && !context.tableOrAliasContext) {
                             const keywords = [
                                'SELECT', 'FROM', 'WHERE', 'JOIN', 'INNER', 'LEFT', 'RIGHT', 'OUTER', 'ON',
                                'GROUP', 'BY', 'HAVING', 'ORDER', 'ASC', 'DESC', 'LIMIT', 'OFFSET',
                                'INSERT', 'INTO', 'VALUES', 'UPDATE', 'SET', 'DELETE', 'CREATE', 'TABLE',
                                'AND', 'OR', 'NOT', 'NULL', 'IS', 'IN', 'LIKE', 'BETWEEN', 'EXISTS',
                                'CASE', 'WHEN', 'THEN', 'ELSE', 'END', 'AS', 'DISTINCT', 'COUNT'
                            ];
                            
                            keywords.forEach(keyword => {
                                suggestions.push({
                                    label: keyword,
                                    kind: monaco.languages.CompletionItemKind.Keyword,
                                    insertText: keyword,
                                    detail: 'Keyword',
                                    sortText: '9_' + keyword, // Lowest priority
                                    range: range
                                });
                            });
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

    // Analyze SQL context with alias support
    analyzeContext: function(textBeforeCursor, fullText, metadata) {
        const context = {
            expectsTable: false,
            expectsColumn: false,
            schemaContext: null,        // "schema."
            tableOrAliasContext: null,  // "table." or "alias."
            aliases: {}                 // map of alias -> full_table_name (e.g. "t" -> "schema.table")
        };

        // 1. Extract aliases from full text
        // Simple regex to find "FROM/JOIN schema.table [AS] alias"
        // Matches: FROM table t, JOIN schema.table AS st, etc.
        const aliasRegex = /(?:FROM|JOIN)\s+([a-zA-Z0-9_."]+)(?:\s+(?:AS\s+)?([a-zA-Z0-9_]+))?/gmi;
        let match;
        while ((match = aliasRegex.exec(fullText)) !== null) {
            const fullTableName = match[1];
            const alias = match[2];
            if (alias && alias.toUpperCase() !== 'ON' && alias.toUpperCase() !== 'WHERE' && alias.toUpperCase() !== 'JOIN') {
                context.aliases[alias.toLowerCase()] = fullTableName;
            }
        }

        // 2. Analyze immediate context before cursor
        const cleanText = textBeforeCursor.replace(/--[^\n]*/g, '').replace(/'[^']*'/g, "''");
        const tokens = cleanText.split(/\s+/).filter(t => t.length > 0);
        
        // Check for dot context (schema. or table. or alias.)
        // We look at the very last part of the text, ignoring whitespace
        const dotMatch = textBeforeCursor.match(/([a-zA-Z0-9_]+)\.\s*$/);
        if (dotMatch) {
            const identifier = dotMatch[1];
            
            // Is this identifier a known schema?
            const isSchema = metadata.tables.some(t => t.schemaName.toLowerCase() === identifier.toLowerCase());
            
            if (isSchema) {
                context.schemaContext = identifier;
            } else {
                context.tableOrAliasContext = identifier;
            }
            return context;
        }

        // Standard token-based context
        const lastToken = (tokens[tokens.length - 1] || '').toUpperCase();
        const secondLastToken = (tokens[tokens.length - 2] || '').toUpperCase();

        // Expect Table
        if (['FROM', 'JOIN', 'UPDATE', 'INTO'].includes(lastToken) ||
            (['INNER', 'LEFT', 'RIGHT', 'OUTER', 'FULL', 'CROSS'].includes(lastToken) && secondLastToken === 'JOIN')) {
            context.expectsTable = true;
            return context;
        }

        // Expect Column
        if (['SELECT', 'WHERE', 'ON', 'HAVING', 'GROUP', 'BY', 'ORDER', 'SET', 'AND', 'OR'].includes(lastToken) ||
            lastToken === ',') {
            context.expectsColumn = true;
            return context;
        }
        
        // Fallback for SELECT list (between SELECT and FROM)
        const upperClean = cleanText.toUpperCase();
        const lastSelect = upperClean.lastIndexOf('SELECT');
        const lastFrom = upperClean.lastIndexOf('FROM');
        if (lastSelect > lastFrom) {
            context.expectsColumn = true;
            return context;
        }

        // Default
        context.expectsColumn = true;
        return context;
    },

    // Dispose of completion provider
    disposeCompletionProvider: function(editorId) {
        if (this.completionDisposables[editorId]) {
            this.completionDisposables[editorId].dispose();
            delete this.completionDisposables[editorId];
            // Also clean up editor instance reference
            if (this.editorInstances[editorId]) {
                delete this.editorInstances[editorId];
            }
            return true;
        }
        return false;
    },

    // Set drag data for HTML5 drag/drop
    setDragData: function(event, dragData) {
        try {
            event.dataTransfer.effectAllowed = 'copy';
            event.dataTransfer.setData('text/plain', dragData);
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
    },

    // Enable drop on Monaco editor
    enableDropOnEditor: function(editorId) {
        try {
            const editorElement = document.getElementById(editorId);
            if (!editorElement) return false;

            const editorContent = editorElement.querySelector('.monaco-editor');
            if (!editorContent) return false;

            const self = this;

            // Allow drop
            editorContent.addEventListener('dragover', function(e) {
                e.preventDefault();
                e.stopPropagation();
                e.dataTransfer.dropEffect = 'copy';
            });

            // Handle drop
            editorContent.addEventListener('drop', function(e) {
                e.preventDefault();
                e.stopPropagation();

                const dragData = e.dataTransfer.getData('text/plain');
                if (!dragData) return;

                // Try to find editor instance
                let editor = self.editorInstances[editorId];
                if (!editor && window.monaco && window.monaco.editor) {
                    const editors = window.monaco.editor.getEditors();
                     editor = editors.find(e => {
                         const domNode = e.getContainerDomNode();
                         return domNode && domNode.id === editorId;
                     });
                }

                if (editor) {
                    const position = editor.getPosition();
                    if (position) {
                        editor.executeEdits('drag-drop', [{
                            range: {
                                startLineNumber: position.lineNumber,
                                startColumn: position.column,
                                endLineNumber: position.lineNumber,
                                endColumn: position.column
                            },
                            text: dragData
                        }]);
                        editor.focus();
                    }
                }
            });
            return true;
        } catch (error) {
            console.error('[Drag/Drop] Error enabling drop:', error);
            return false;
        }
    }
};

console.log('[SQL Autocomplete] Helpers loaded successfully');
