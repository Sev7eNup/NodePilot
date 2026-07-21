using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NodePilot.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceSqlServerOrdinalIdentityCollation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
                return;

            migrationBuilder.Sql("""
                DROP INDEX [IX_ExternalIdentities_Authority_Subject] ON [ExternalIdentities];
                ALTER TABLE [ExternalIdentities] ALTER COLUMN [Authority] nvarchar(384) COLLATE Latin1_General_100_BIN2 NOT NULL;
                ALTER TABLE [ExternalIdentities] ALTER COLUMN [Subject] nvarchar(384) COLLATE Latin1_General_100_BIN2 NOT NULL;
                CREATE UNIQUE INDEX [IX_ExternalIdentities_Authority_Subject]
                    ON [ExternalIdentities] ([Authority], [Subject]);

                DROP INDEX [IX_ScimGroups_Authority_ExternalId] ON [ScimGroups];
                ALTER TABLE [ScimGroups] ALTER COLUMN [Authority] nvarchar(384) COLLATE Latin1_General_100_BIN2 NOT NULL;
                ALTER TABLE [ScimGroups] ALTER COLUMN [ExternalId] nvarchar(384) COLLATE Latin1_General_100_BIN2 NOT NULL;
                CREATE UNIQUE INDEX [IX_ScimGroups_Authority_ExternalId]
                    ON [ScimGroups] ([Authority], [ExternalId]);

                DROP INDEX [IX_DirectoryMemberships_Authority_GroupKey] ON [DirectoryMemberships];
                DROP INDEX [IX_DirectoryMemberships_UserId_Authority_GroupKey] ON [DirectoryMemberships];
                ALTER TABLE [DirectoryMemberships] ALTER COLUMN [Authority] nvarchar(384) COLLATE Latin1_General_100_BIN2 NOT NULL;
                ALTER TABLE [DirectoryMemberships] ALTER COLUMN [GroupKey] nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL;
                CREATE INDEX [IX_DirectoryMemberships_Authority_GroupKey]
                    ON [DirectoryMemberships] ([Authority], [GroupKey]);
                CREATE UNIQUE INDEX [IX_DirectoryMemberships_UserId_Authority_GroupKey]
                    ON [DirectoryMemberships] ([UserId], [Authority], [GroupKey]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Microsoft.EntityFrameworkCore.SqlServer")
                return;

            migrationBuilder.Sql("""
                IF EXISTS (
                    SELECT 1 FROM [ExternalIdentities]
                    GROUP BY [Authority] COLLATE DATABASE_DEFAULT, [Subject] COLLATE DATABASE_DEFAULT
                    HAVING COUNT_BIG(*) > 1)
                    THROW 51001, 'Cannot downgrade identity collation: case-distinct external identities collide under DATABASE_DEFAULT.', 1;

                IF EXISTS (
                    SELECT 1 FROM [ScimGroups]
                    GROUP BY [Authority] COLLATE DATABASE_DEFAULT, [ExternalId] COLLATE DATABASE_DEFAULT
                    HAVING COUNT_BIG(*) > 1)
                    THROW 51002, 'Cannot downgrade identity collation: case-distinct SCIM groups collide under DATABASE_DEFAULT.', 1;

                IF EXISTS (
                    SELECT 1 FROM [DirectoryMemberships]
                    GROUP BY [UserId], [Authority] COLLATE DATABASE_DEFAULT, [GroupKey] COLLATE DATABASE_DEFAULT
                    HAVING COUNT_BIG(*) > 1)
                    THROW 51003, 'Cannot downgrade identity collation: case-distinct memberships collide under DATABASE_DEFAULT.', 1;

                DROP INDEX [IX_ExternalIdentities_Authority_Subject] ON [ExternalIdentities];
                ALTER TABLE [ExternalIdentities] ALTER COLUMN [Authority] nvarchar(384) COLLATE DATABASE_DEFAULT NOT NULL;
                ALTER TABLE [ExternalIdentities] ALTER COLUMN [Subject] nvarchar(384) COLLATE DATABASE_DEFAULT NOT NULL;
                CREATE UNIQUE INDEX [IX_ExternalIdentities_Authority_Subject]
                    ON [ExternalIdentities] ([Authority], [Subject]);

                DROP INDEX [IX_ScimGroups_Authority_ExternalId] ON [ScimGroups];
                ALTER TABLE [ScimGroups] ALTER COLUMN [Authority] nvarchar(384) COLLATE DATABASE_DEFAULT NOT NULL;
                ALTER TABLE [ScimGroups] ALTER COLUMN [ExternalId] nvarchar(384) COLLATE DATABASE_DEFAULT NOT NULL;
                CREATE UNIQUE INDEX [IX_ScimGroups_Authority_ExternalId]
                    ON [ScimGroups] ([Authority], [ExternalId]);

                DROP INDEX [IX_DirectoryMemberships_Authority_GroupKey] ON [DirectoryMemberships];
                DROP INDEX [IX_DirectoryMemberships_UserId_Authority_GroupKey] ON [DirectoryMemberships];
                ALTER TABLE [DirectoryMemberships] ALTER COLUMN [Authority] nvarchar(384) COLLATE DATABASE_DEFAULT NOT NULL;
                ALTER TABLE [DirectoryMemberships] ALTER COLUMN [GroupKey] nvarchar(256) COLLATE DATABASE_DEFAULT NOT NULL;
                CREATE INDEX [IX_DirectoryMemberships_Authority_GroupKey]
                    ON [DirectoryMemberships] ([Authority], [GroupKey]);
                CREATE UNIQUE INDEX [IX_DirectoryMemberships_UserId_Authority_GroupKey]
                    ON [DirectoryMemberships] ([UserId], [Authority], [GroupKey]);
                """);
        }
    }
}
