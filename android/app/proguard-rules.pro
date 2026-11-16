# ShizukuGrants 用反射调用 Shizuku.newProcess(...)，R8 不能改名或内联
-keep class rikka.shizuku.Shizuku { *; }
