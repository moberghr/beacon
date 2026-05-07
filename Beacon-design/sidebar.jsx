function Sidebar({ active = "Home" }) {
  const sections = [
    {
      label: "Overview",
      items: [
        { name: "Home", icon: "Home" },
        { name: "Control Tower", icon: "Tower" },
        { name: "Dashboards", icon: "Grid" },
      ],
    },
    {
      label: "Data",
      items: [
        { name: "Data Sources", icon: "Database", count: 12 },
        { name: "Projects", icon: "Folder", count: 4 },
        { name: "Data Quality", icon: "Shield", badge: "NEW" },
        { name: "Data Migration", icon: "ArrowsLR" },
        { name: "AI Actors", icon: "Bot" },
      ],
    },
    {
      label: "Alerts",
      items: [
        { name: "Queries", icon: "Query", count: 38 },
        { name: "Subscriptions", icon: "Inbox" },
        { name: "Notifications", icon: "Bell", count: 3 },
        { name: "Recipients", icon: "Users" },
        { name: "Tasks", icon: "Check", count: 7 },
      ],
    },
    {
      label: "MCP",
      items: [
        { name: "Data Catalog", icon: "Book" },
        { name: "API Keys", icon: "Key" },
        { name: "MCP Settings", icon: "Sliders" },
        { name: "MCP Playground", icon: "Wand" },
        { name: "MCP Learning", icon: "Lightbulb" },
      ],
    },
    {
      label: "Admin",
      items: [
        { name: "User Management", icon: "Users" },
        { name: "Admin Settings", icon: "Cog" },
      ],
    },
  ];

  return (
    <aside className="sidebar">
      <div className="sidebar__brand">
        <div className="sidebar__logo">
          <div className="sidebar__logo-dot" aria-hidden="true"></div>
          <span>Beacon</span>
        </div>
        <span className="sidebar__version">v0.93</span>
      </div>

      <nav className="sidebar__nav">
        {sections.map((s) => (
          <div className="sidebar__section" key={s.label}>
            <div className="sidebar__section-label">{s.label}</div>
            {s.items.map((item) => {
              const IconCmp = Icon[item.icon];
              const isActive = item.name === active;
              return (
                <button
                  key={item.name}
                  className={"nav-item" + (isActive ? " active" : "")}
                >
                  {IconCmp && <IconCmp className="nav-item__icon" />}
                  <span>{item.name}</span>
                  {item.badge && <span className="nav-item__badge">{item.badge}</span>}
                  {item.count != null && !item.badge && (
                    <span className="nav-item__count">{item.count}</span>
                  )}
                </button>
              );
            })}
          </div>
        ))}
        <div className="sidebar__section" style={{ marginTop: 18 }}>
          <button className="nav-item">
            <Icon.Info className="nav-item__icon" />
            <span>About</span>
          </button>
        </div>
      </nav>

      <div className="sidebar__footer">
        <div className="user-chip">
          <div className="user-chip__avatar">MR</div>
          <div className="user-chip__info">
            <div className="user-chip__name">mirko</div>
            <div className="user-chip__role">Admin · moberg.hr</div>
          </div>
          <Icon.ChevronDown size={14} className="muted" />
        </div>
      </div>
    </aside>
  );
}

window.Sidebar = Sidebar;
