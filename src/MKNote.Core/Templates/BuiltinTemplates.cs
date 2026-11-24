using MKNote.Core.Models;

namespace MKNote.Core.Templates;

/// <summary>
/// 内置 10 套常用笔记模板（支持 Markdown 与富文本两种格式），支持 {{date}}/{{time}}/{{weekday}} 动态变量展开。
/// </summary>
public static class BuiltinTemplates
{
    public static string ExpandVariables(string pattern, DateTimeOffset? now = null)
    {
        var dt = (now ?? DateTimeOffset.Now).LocalDateTime;
        var weekday = dt.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            _ => "星期日"
        };

        return pattern
            .Replace("%date%", dt.ToString("yyyy-MM-dd"))
            .Replace("{{date}}", dt.ToString("yyyy-MM-dd"))
            .Replace("{{time}}", dt.ToString("HH:mm"))
            .Replace("{{datetime}}", dt.ToString("yyyy-MM-dd HH:mm"))
            .Replace("{{weekday}}", weekday);
    }

    public static IReadOnlyList<NoteTemplateItem> GetAll() =>
    [
        // 1. 会议记录
        new()
        {
            Id = "tpl_meeting_md", Name = "会议记录 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "会议记录 [{{date}}]", DefaultTags = ["工作", "会议", "记录"],
            DefaultBackgroundColor = "sky_blue", IsBuiltin = true, SortOrder = 1,
            Content = """
                ---
                tags: [工作, 会议, 记录]
                created: {{date}}
                ---
                # 会议记录 - {{date}}

                > [!info] 会议基本信息
                > - **时间**：{{datetime}} {{weekday}}
                > - **地点**：
                > - **参会人**：
                > - **主持人**：

                ## 一、会议议程
                1. 
                2. 

                ## 二、讨论要点
                - 

                ## 三、决议与行动项（Action Items）
                - [ ] 负责人： | 任务描述： | 截止日期：
                - [ ] 负责人： | 任务描述： | 截止日期：

                ## 四、关联笔记
                - 相关项目：[[]]
                """
        },
        new()
        {
            Id = "tpl_meeting_rich", Name = "会议记录 (富文本)", DocFormat = DocumentFormat.RichHtml,
            DefaultTitlePattern = "会议记录 [%date%]", DefaultTags = ["工作", "会议", "记录"],
            DefaultBackgroundColor = "sky_blue", IsBuiltin = true, SortOrder = 2,
            Content = """
                <h2>会议记录（{{date}}）</h2>
                <table><tbody>
                  <tr><th>会议时间</th><td>{{datetime}} {{weekday}}</td><th>会议地点</th><td></td></tr>
                  <tr><th>主持人</th><td></td><th>参会人员</th><td></td></tr>
                </tbody></table>
                <h3>一、会议议题</h3><p></p>
                <h3>二、讨论纪要</h3><ul><li></li></ul>
                <h3>三、待办事项</h3><ul data-type="taskList"><li data-type="taskItem" data-checked="false"><p>待办任务 1</p></li></ul>
                """
        },
        // 2. 九宫格日记
        new()
        {
            Id = "tpl_9grid_md", Name = "九宫格日记 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "九宫格日记 [{{date}}]", DefaultTags = ["日记", "九宫格"],
            DefaultBackgroundColor = "parchment", IsBuiltin = true, SortOrder = 3,
            Content = """
                ---
                tags: [日记, 九宫格]
                date: {{date}}
                ---
                # 九宫格日记 · {{date}} {{weekday}}

                | 开心时刻 | 感恩的人与事 | 今日成就 |
                | :--- | :--- | :--- |
                |  |  |  |

                | 计划与工作 | **{{date}} 今日关键词** | 阅读与学习 |
                | :--- | :---: | :--- |
                |  | ☀️ |  |

                | 健康与运动 | 反思与改进 | 明日期待 |
                | :--- | :--- | :--- |
                |  |  |  |
                """
        },
        // 3. 工作计划
        new()
        {
            Id = "tpl_workplan_md", Name = "工作计划 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "工作计划 [{{date}}]", DefaultTags = ["工作", "计划"],
            DefaultBackgroundColor = "default", IsBuiltin = true, SortOrder = 4,
            Content = """
                # 工作计划 · {{date}}

                ## 核心目标（Top 3）
                1. 
                2. 
                3. 

                ## 任务清单
                - [ ] 【P0】
                - [ ] 【P1】
                - [ ] 【P2】

                ## 风险与阻碍
                > [!warning] 需要提前协调的事项
                > - 
                """
        },
        // 4. 康奈尔笔记
        new()
        {
            Id = "tpl_cornell_md", Name = "康奈尔笔记 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "康奈尔笔记 [{{date}}]", DefaultTags = ["学习", "记录"],
            DefaultBackgroundColor = "eye_green", IsBuiltin = true, SortOrder = 5,
            Content = """
                # 主题：

                > [!note] 线索栏（关键词 / 核心问题）
                > - 问题 1：
                > - 问题 2：

                ## 笔记主栏（详细记录）
                - 

                ## 底部总结栏（用 3 句话概括核心结论）
                > [!tip] 总结
                > 
                """
        },
        // 5. 四象限法则
        new()
        {
            Id = "tpl_quadrant_md", Name = "四象限法则 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "四象限法则 [{{date}}]", DefaultTags = ["分析", "时间管理"],
            DefaultBackgroundColor = "default", IsBuiltin = true, SortOrder = 6,
            Content = """
                # 艾森豪威尔四象限 · {{date}}

                ## 🔥 第一象限：重要且紧急（立刻执行）
                - [ ] 

                ## 🎯 第二象限：重要不紧急（重点规划）
                - [ ] 

                ## ⚡ 第三象限：不重要但紧急（授权/快速处理）
                - [ ] 

                ## 🧊 第四象限：不重要不紧急（延后或删除）
                - [ ] 
                """
        },
        // 6. SWOT 分析
        new()
        {
            Id = "tpl_swot_md", Name = "SWOT 分析 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "SWOT 分析 [{{date}}]", DefaultTags = ["分析"],
            DefaultBackgroundColor = "lilac", IsBuiltin = true, SortOrder = 7,
            Content = """
                # SWOT 分析

                | 内部优势 (Strengths) | 内部劣势 (Weaknesses) |
                | :--- | :--- |
                | -  | -  |

                | 外部机会 (Opportunities) | 外部威胁 (Threats) |
                | :--- | :--- |
                | -  | -  |

                ## 行动策略
                - **SO 策略（增长）**：
                - **WO 策略（扭转）**：
                - **ST 策略（多元化）**：
                - **WT 策略（防御）**：
                """
        },
        // 7. 5W1H 工作法
        new()
        {
            Id = "tpl_5w1h_md", Name = "5W1H 工作法 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "5W1H 分析 [{{date}}]", DefaultTags = ["分析", "工作"],
            DefaultBackgroundColor = "default", IsBuiltin = true, SortOrder = 8,
            Content = """
                # 5W1H 分析 · {{date}}

                - **What（做什么）**：
                - **Why（为什么做）**：
                - **Who（谁来做）**：
                - **When（何时完成）**：
                - **Where（在哪里做）**：
                - **How（如何执行）**：
                """
        },
        // 8. 出行清单
        new()
        {
            Id = "tpl_travel_md", Name = "出行清单 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "出行清单 [{{date}}]", DefaultTags = ["计划", "出行"],
            DefaultBackgroundColor = "sakura", IsBuiltin = true, SortOrder = 9,
            Content = """
                # 出行清单 · {{date}}

                ## 证件与资金
                - [ ] 身份证 / 护照
                - [ ] 驾驶证 / 银行卡

                ## 数码设备
                - [ ] 手机 & 充电器 & 充电宝
                - [ ] 耳机 / 笔记本电脑

                ## 衣物与日用品
                - [ ] 换洗衣物
                - [ ] 洗漱用品 / 常用药品
                """
        },
        // 9. 读书卡
        new()
        {
            Id = "tpl_book_md", Name = "读书卡 (Markdown)", DocFormat = DocumentFormat.Markdown,
            DefaultTitlePattern = "读书卡 [{{date}}]", DefaultTags = ["读书"],
            DefaultBackgroundColor = "parchment", IsBuiltin = true, SortOrder = 10,
            Content = """
                ---
                tags: [读书]
                author: ""
                rating: "⭐⭐⭐⭐⭐"
                finished: {{date}}
                ---
                # 《书名》读书卡

                ## 核心观点
                - 

                ## 精彩摘录
                > 

                ## 我的思考与行动
                - 
                """
        },
        // 10. 课程表
        new()
        {
            Id = "tpl_schedule_rich", Name = "课程表 / 周计划表 (富文本)", DocFormat = DocumentFormat.RichHtml,
            DefaultTitlePattern = "课程表", DefaultTags = ["计划"],
            DefaultBackgroundColor = "cool_gray", IsBuiltin = true, SortOrder = 11,
            Content = """
                <h2>课程表 / 周计划</h2>
                <table><tbody>
                  <tr><th>时段</th><th>周一</th><th>周二</th><th>周三</th><th>周四</th><th>周五</th></tr>
                  <tr><td>上午</td><td></td><td></td><td></td><td></td><td></td></tr>
                  <tr><td>下午</td><td></td><td></td><td></td><td></td><td></td></tr>
                  <tr><td>晚上</td><td></td><td></td><td></td><td></td><td></td></tr>
                </tbody></table>
                """
        }
    ];
}
