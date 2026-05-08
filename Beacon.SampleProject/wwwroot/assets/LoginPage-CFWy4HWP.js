import{u as b,g as f,h as j,r as u,j as e,f as w,A as y}from"./index-DJkGJ8aZ.js";import{u as v,a as S,o as N,b as k,s as m}from"./schemas-Dy7JEHuj.js";const E=N({username:m().trim().min(1,"Username is required"),password:m().min(1,"Password is required"),rememberMe:k()});function M(){var c;const o=b(),[p]=f(),h=p.get("ssoError"),d=j(),[l,i]=u.useState(h?"Single sign-on failed. Please try again or sign in with username and password.":null),{register:n,handleSubmit:x,formState:{errors:t,isSubmitting:s}}=v({resolver:S(E),defaultValues:{username:"",password:"",rememberMe:!1}});u.useEffect(()=>{var a;(a=d.data)!=null&&a.isAuthenticated&&o("/home",{replace:!0})},[(c=d.data)==null?void 0:c.isAuthenticated,o]);async function g(a){i(null);try{const r=await w("/beacon/api/auth/login",{method:"POST",body:JSON.stringify({username:a.username,password:a.password,rememberMe:a.rememberMe})});if(!r.success){i(r.error||"Invalid username or password.");return}window.location.href=r.redirectUrl||"/app/home"}catch(r){i(r instanceof y?r.body||r.message:"Login failed. Try again.")}}return e.jsxs("div",{className:"auth-shell",children:[e.jsxs("div",{className:"auth-card",children:[e.jsx("h1",{className:"auth-title",children:"Sign in"}),e.jsx("p",{className:"muted",style:{textAlign:"center",marginBottom:24},children:"Sign in to your Beacon workspace"}),l&&e.jsx("div",{className:"auth-alert auth-alert--error",role:"alert",children:l}),e.jsx("a",{className:"btn btn--primary",href:"/beacon/api/auth/sso/challenge",style:{width:"100%",justifyContent:"center"},children:"Continue with single sign-on"}),e.jsx("div",{className:"auth-divider",children:e.jsx("span",{children:"or"})}),e.jsxs("form",{onSubmit:x(g),noValidate:!0,children:[e.jsxs("label",{className:"auth-field",children:[e.jsx("span",{children:"Username"}),e.jsx("input",{className:"input",type:"text",autoComplete:"username",disabled:s,...n("username")}),t.username&&e.jsx("span",{className:"auth-error",children:t.username.message})]}),e.jsxs("label",{className:"auth-field",children:[e.jsx("span",{children:"Password"}),e.jsx("input",{className:"input",type:"password",autoComplete:"current-password",disabled:s,...n("password")}),t.password&&e.jsx("span",{className:"auth-error",children:t.password.message})]}),e.jsxs("label",{className:"auth-checkbox",children:[e.jsx("input",{type:"checkbox",...n("rememberMe"),disabled:s}),e.jsx("span",{children:"Remember me"})]}),e.jsx("button",{type:"submit",className:"btn btn--primary",style:{width:"100%",justifyContent:"center",marginTop:16},disabled:s,children:s?"Signing in…":"Sign in"})]})]}),e.jsx("style",{children:A})]})}const A=`
  .auth-shell {
    min-height: 100vh;
    display: grid;
    place-items: center;
    padding: 24px;
    background: var(--bg, #0f1423);
  }
  .auth-card {
    width: 100%;
    max-width: 420px;
    background: var(--surface, rgba(15, 20, 35, 0.85));
    border: 1px solid var(--border, rgba(255, 255, 255, 0.08));
    border-radius: 16px;
    padding: 32px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
  }
  .auth-title {
    font-size: 22px;
    margin: 0 0 8px;
    text-align: center;
    color: var(--text);
  }
  .auth-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
    margin-bottom: 12px;
  }
  .auth-field > span {
    font-size: 13px;
    color: var(--muted);
  }
  .auth-error {
    font-size: 12px;
    color: var(--crit, #c00);
    margin-top: 2px;
  }
  .auth-checkbox {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 13px;
    color: var(--muted);
    margin: 8px 0 0;
  }
  .auth-divider {
    display: flex;
    align-items: center;
    margin: 20px 0;
    color: var(--muted);
    font-size: 12px;
  }
  .auth-divider::before, .auth-divider::after {
    content: '';
    flex: 1;
    border-top: 1px solid var(--border);
  }
  .auth-divider > span {
    padding: 0 12px;
  }
  .auth-alert {
    padding: 10px 12px;
    border-radius: 8px;
    margin-bottom: 16px;
    font-size: 13px;
  }
  .auth-alert--error {
    background: rgba(220, 38, 38, 0.12);
    border: 1px solid rgba(220, 38, 38, 0.4);
    color: #fca5a5;
  }
  .auth-alert--info {
    background: rgba(59, 130, 246, 0.12);
    border: 1px solid rgba(59, 130, 246, 0.4);
    color: #93c5fd;
  }
  .auth-alert--ok {
    background: rgba(16, 185, 129, 0.12);
    border: 1px solid rgba(16, 185, 129, 0.4);
    color: #6ee7b7;
  }
`;export{A as AUTH_STYLES,M as default};
//# sourceMappingURL=LoginPage-CFWy4HWP.js.map
