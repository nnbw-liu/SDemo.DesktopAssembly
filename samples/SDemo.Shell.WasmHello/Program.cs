// wasm 组件样例进程：组件由宿主渲染，这个进程只在「ensure / MCP 需要」时启动。
// 它不建窗口、不跑业务，只验证 app.json 的 executable 与哈希链路。
Console.WriteLine("SDemo.Shell.WasmHello: wasm 组件的进程侧样例（渲染由宿主负责）。");
return 0;
