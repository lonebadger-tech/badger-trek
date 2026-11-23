ALTER TABLE "Blogs" DROP COLUMN "Rating"
/

ALTER TABLE "Posts" ADD "Title" NVARCHAR2(2000) NOT NULL
/

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = N'20261123183037_RatingDropTitle'
/

DROP INDEX "IX_Blogs_Name"
/

ALTER TABLE "Blogs" DROP COLUMN "Url"
/

declare
   l_nullable user_tab_columns.nullable % type;
begin 
   select nullable into l_nullable 
   from user_tab_columns 
  where table_name = 'Blogs' 
  and column_name = 'Name' 
;
   if l_nullable = 'N' then 
        EXECUTE IMMEDIATE 'ALTER TABLE "Blogs" MODIFY "Name" NVARCHAR2(2000) ';
 else 
        EXECUTE IMMEDIATE 'ALTER TABLE "Blogs" MODIFY "Name" NVARCHAR2(2000) NOT NULL';
 end if;
end;
/

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = N'20261123183032_AddBlogUrl'
/

DROP TABLE "Posts"
/

DROP TABLE "Blogs"
/

DELETE FROM "__EFMigrationsHistory"
WHERE "MigrationId" = N'20261123183027_Initial'
/

