# Madoka C# Code Assister

Visual Studio 17.14+ 扩展，提供一组 C# 类成员编辑工具，帮助开发者快速完成常见的代码重构与文档格式化操作。

## 功能

所有命令均作用于当前光标所在的类型，仅在 C# 编辑器中可用。

| 快捷键 | 命令 | 说明 |
| --- | --- | --- |
| `Ctrl+1+1` | 格式化 XML 注释 | 将多行 XML 文档注释压缩为单行 |
| `Ctrl+2+2` | 属性私有化 setter | 将类内属性 `set` 改为 `private set` |
| `Ctrl+3+3` | 属性公有化 setter | 将类内属性 `private set` 改为 `set` |
| `Ctrl+4+4` | 生成构造函数 | 生成私有空构造函数 + 含全部可赋值属性参数的公有构造函数 |
| `Ctrl+5+5` | 移除属性特性 | 移除 `Required`、`StringLength`、`MaxLength`、`MinLength`、`Display`、`Description` 等特性 |
| `Ctrl+6+6` | 生成 CreateFrom 映射 | 在空 `CreateFrom` 方法内生成源类型到目标类型的属性映射代码 |

所有命令也可通过 **扩展(Extensions) 菜单**触发。

## 环境要求

- Visual Studio 2022 17.14 及以上（< 18.0）
- .NET SDK 8.0

## 构建与安装

```powershell
# 构建并打包 VSIX
dotnet build Madoka.CSharpCodeAssister.csproj -c Release

# 输出文件
# bin\Release\net8.0-windows\Madoka.CSharpCodeAssister.vsix
```

双击生成的 `.vsix` 文件或通过 `vsixinstaller.exe` 安装扩展。

## 项目结构

```
Madoka.CSharpCodeAssister/
├── Extension.cs                        # 扩展入口点与元数据
├── *.Command.cs                        # 六个命令入口（VS 命令层）
├── ClassConstructorGenerator.cs        # 构造函数生成逻辑
├── CreateFromMappingGenerator.cs       # CreateFrom 映射生成逻辑
├── CreateFromSourceFileResolver.cs     # 跨项目源文件解析
├── CreateFromProjectCompilationBuilder.cs  # 跨项目编译上下文构建
├── PropertyAttributeRemover.cs         # 属性特性移除逻辑
├── Property*SetterConverter.cs         # setter 可见性转换逻辑
├── ClassSpanFinder.cs                  # 基于 Roslyn 的类型范围定位
├── XmlDocCommentFormatter.cs           # XML 注释格式化逻辑
├── .vsextension/                       # 扩展清单
└── _verify/                            # 独立验证控制台程序
```

## CreateFrom 映射示例

在光标置于空方法体内时触发 `Ctrl+6+6`：

```csharp
public static ProductResponse CreateFrom(Product source)
{
    var result = new ProductResponse
    {
        Id = source.Id,
        Name = source.Name,
        Price = source.Price,
    };

    return result;
}
```

生成逻辑按以下优先级执行：

1. 语法树级匹配（当前文档 + 引用的源文件）
2. 语义模型匹配（编译整个解决方案后）
3. 失败时给出友好诊断提示

## 验证程序

`_verify` 为独立控制台项目，用于在无 VS 环境下验证生成逻辑：

```powershell
dotnet run --project _verify -- <C#文件路径>
# 或设置环境变量 CREATE_FROM_TEST_FILE 后直接运行
```

## 技术说明

- 基于 `Microsoft.VisualStudio.Extensibility.Sdk`（17.14）新式扩展模型，进程外托管
- 使用 Roslyn（`Microsoft.CodeAnalysis.CSharp` 4.12）进行语法与语义分析
- 核心逻辑均为静态类，不依赖 UI 层，便于独立测试
- 目标框架：`net8.0-windows`