using T3.Editor.SkillQuest.Data;

namespace T3.Editor.SkillQuest;

internal static partial class SkillManager
{
    private static List<QuestTopic> CreateMockLevelStructure()
    {
        return
            [
                new QuestTopic
                    {
                        Title = "Welcome",
                        Id = new Guid("D5E76A36-DEB8-42D8-A1BB-6B85B7848662"),
                        Levels =
                            [
                                new QuestLevel
                                    {
                                        Title = "Let's get started",
                                        SymbolId = new Guid("4f9eb54f-1b81-4a6b-a842-f80c423e5843")
                                    },
                                new QuestLevel
                                    {
                                        Title = "Move it!",
                                        SymbolId = new Guid("78BDD03E-971C-4A75-80DB-74A6B5AC946F")
                                    },
                                new QuestLevel
                                    {
                                        Title = "It's there after all",
                                        SymbolId = new Guid("94C9874C-2594-482B-A0B3-10D8CD414475")
                                    },
                            ]
                    },
                new QuestTopic
                    {
                        Title = "Math",
                        Id = new Guid("AE01DCC2-1382-4771-B6E4-51ED915D610E"),
                        Levels =
                            [
                                new QuestLevel
                                    {
                                        Title = "Modulo",
                                        SymbolId = new Guid("DDE99CFA-DD2C-43B8-8680-E54963884763")
                                    },
                                new QuestLevel
                                    {
                                        Title = "Make it jump",
                                        SymbolId = new Guid("4A58764E-DB14-40F2-BBBC-90BF0D82FA5C")
                                    },
                                new QuestLevel
                                    {
                                        Title = "I love these curves...",
                                        SymbolId = new Guid("473FA8BD-667F-48C0-A5BF-3983246BCC4A")
                                    },
                            ]
                    },
                new QuestTopic
                    {
                        Title = "More fun",
                        Id = new Guid("AE01DCC2-1382-4771-B6E4-51ED915D610E"),
                        Levels =
                            [
                                new QuestLevel
                                    {
                                        Title = "Let's get started",
                                        SymbolId = new Guid("DFE3C08D-CB18-4780-B65B-1D230BA4C39E")
                                    },
                                new QuestLevel
                                    {
                                        Title = "Move it!",
                                        SymbolId = new Guid("09FAAF25-AA00-4B80-9B9C-5542E1F3382D")
                                    },
                                new QuestLevel
                                    {
                                        Title = "It's there after all",
                                        SymbolId = new Guid("89850A93-FAB5-413B-8C96-186F9A9CF1E2")
                                    },
                            ]
                    },
            ];
    }
}