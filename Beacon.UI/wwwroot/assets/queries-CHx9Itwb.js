import{n as e,o as t}from"./index-hfqr-HVz.js";function o(){return e({queryKey:["notifications"],queryFn:()=>t().getNotifications(0,100,void 0,void 0)})}function a(i){return e({queryKey:["notifications",i],queryFn:async()=>await t().getNotificationDetail(i),enabled:typeof i=="number"&&Number.isFinite(i)})}export{a,o as u};
//# sourceMappingURL=queries-CHx9Itwb.js.map
