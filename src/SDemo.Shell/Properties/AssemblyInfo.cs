using System.Runtime.CompilerServices;

// P2 起测试需要构造 AppEntry 的运行时字段（state / runtime / lastError）来验证诊断与权限行为。
[assembly: InternalsVisibleTo("SDemo.Shell.Tests")]
