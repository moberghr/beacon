# Quick Authorization Test Guide

## 🚀 Start the Application

```bash
cd Semantico.SampleProject
dotnet run
```

Then navigate to: **https://localhost:7187/semantico**

## 👥 Test Users

### 1. Admin User (Full Access)
- **Username:** `admin`
- **Password:** `admin` (or any password)
- **Expected behavior:**
  - Can view all pages
  - Can create/modify/delete resources
  - Can execute queries
  - Can manage subscriptions

### 2. Editor User (Read/Write)
- **Username:** `editor`
- **Password:** `editor` (or any password)
- **Expected behavior:**
  - Can view all pages
  - Can create/modify resources
  - **Cannot delete** resources
  - Can execute queries

### 3. Viewer User (Read-Only)
- **Username:** `viewer`
- **Password:** `viewer` (or any password)
- **Expected behavior:**
  - Can view all pages
  - **Cannot create/modify/delete** resources
  - Can execute read-only queries
  - **Cannot modify** subscriptions

### 4. Any Other User (Defaults to Viewer)
- **Username:** `test`
- **Password:** `anything`
- **Expected behavior:** Same as Viewer role

## ✅ What to Verify

1. **User Display:**
   - Look at the top-right corner
   - Should show your actual username (not "Admin")
   - Avatar should show first letter of username

2. **Try Write Operations:**
   - Login as `viewer`
   - Try to create a new data source (should be blocked with 403 Forbidden)
   - Login as `editor`
   - Try to create a new data source (should work)

3. **Test Different Roles:**
   - Logout and login with different usernames
   - Notice how permissions change

## 🔧 How It Works

1. **BasicAuth** accepts any username/password
2. **ClaimsTransformer** assigns role based on username:
   - `admin` → Admin role
   - `editor` → Editor role
   - `viewer` → Viewer role
   - Others → Viewer role (default)
3. **AuthorizationProvider** enforces permissions:
   - Checks role claims
   - Returns 403 if unauthorized

## 📝 Implementation Files

- `Program.cs` - Configuration
- `SampleAuthorizationProvider.cs` - Permission logic
- `SampleClaimsTransformation.cs` - Role assignment
- `AUTHORIZATION_EXAMPLE.md` - Full documentation

## 🐛 If Something's Not Working

1. Check console output for errors
2. Verify you're accessing `/semantico` path
3. Clear browser cookies if stuck
4. Check that authorization is enabled in `Program.cs`:
   ```csharp
   options.Authorization.Enabled = true;
   ```
