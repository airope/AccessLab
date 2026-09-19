using Microsoft.EntityFrameworkCore.Migrations;
#nullable disable
namespace AccessLab.Api.Migrations;

public partial class DatabaseInvariants : Migration
{
    protected override void Up(MigrationBuilder m)
    {
        m.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist");
        m.Sql(
            """
            ALTER TABLE "Memberships" ADD CONSTRAINT membership_positive_period CHECK ("StartsAt" < "EndsAt");
            ALTER TABLE "Memberships" ADD CONSTRAINT membership_no_overlap EXCLUDE USING gist
            ("AccountId" WITH =, "GroupId" WITH =, tstzrange("StartsAt", "EndsAt", '[)') WITH &&) WHERE ("Status" = 'Active');
            ALTER TABLE "Memberships" ADD CONSTRAINT membership_account_fk FOREIGN KEY ("AccountId") REFERENCES "Accounts"("Id");
            ALTER TABLE "Memberships" ADD CONSTRAINT membership_group_fk FOREIGN KEY ("GroupId") REFERENCES "Groups"("Id");
            CREATE FUNCTION accesslab_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Append-only application journal' USING ERRCODE='23000'; END;
            $$;
            CREATE TRIGGER immutable_audit BEFORE UPDATE OR DELETE ON "Audit" FOR EACH ROW EXECUTE FUNCTION accesslab_append_only();
            CREATE TRIGGER immutable_projection BEFORE UPDATE OR DELETE ON "Projections" FOR EACH ROW EXECUTE FUNCTION accesslab_append_only();
            """
        );
    }

    protected override void Down(MigrationBuilder m)
    {
        m.Sql(
            """
            DROP TRIGGER immutable_audit ON "Audit"; DROP TRIGGER immutable_projection ON "Projections"; DROP FUNCTION accesslab_append_only();
            ALTER TABLE "Memberships" DROP CONSTRAINT membership_positive_period, DROP CONSTRAINT membership_no_overlap, DROP CONSTRAINT membership_account_fk, DROP CONSTRAINT membership_group_fk;
            """
        );
    }
}
