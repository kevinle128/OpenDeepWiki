\set ON_ERROR_STOP on
BEGIN;
SET LOCAL lock_timeout = '5s';
SET LOCAL statement_timeout = '30s';

UPDATE "SystemSettings"
SET "Value" = 'en,vi', "UpdatedAt" = now()
WHERE "Key" = 'WIKI_LANGUAGES' AND NOT "IsDeleted";

-- The worker stopped this task during shutdown. Keep it cancelled after restart.
UPDATE "BranchGenerationTasks"
SET "Status" = 4, "ErrorMessage" = 'Cancelled by user', "UpdatedAt" = now()
WHERE "Id" = '5ebca651-b805-4a01-ac76-143014ca1dc2'
  AND "Status" = 3
  AND "ErrorMessage" = 'Branch generation was cancelled while processing';

UPDATE "RepositoryBranches" b
SET "GenerationStatus" = 4, "LastGenerationError" = NULL, "UpdatedAt" = now()
FROM "BranchGenerationTasks" t
WHERE t."Id" = '5ebca651-b805-4a01-ac76-143014ca1dc2'
  AND t."Status" = 4
  AND b."LastGenerationTaskId" = t."Id";

-- Resume the queued battle job if the configuration shutdown interrupted it.
UPDATE "BranchGenerationTasks"
SET "Status" = 0, "ErrorMessage" = NULL, "StartedAt" = NULL,
    "CompletedAt" = NULL, "UpdatedAt" = now()
WHERE "Id" = '675185cc-cc72-4beb-a662-370509dbfbf3'
  AND "Status" = 3
  AND "ErrorMessage" = 'Branch generation was cancelled while processing';

UPDATE "RepositoryBranches" b
SET "GenerationStatus" = 0, "LastGenerationError" = NULL, "UpdatedAt" = now()
FROM "BranchGenerationTasks" t
WHERE t."Id" = '675185cc-cc72-4beb-a662-370509dbfbf3'
  AND t."Status" = 0
  AND b."LastGenerationTaskId" = t."Id";

UPDATE "BranchLanguages" bl
SET "IsDeleted" = true, "IsDefault" = false,
    "DeletedAt" = now(), "UpdatedAt" = now()
FROM "RepositoryBranches" b, "Repositories" r
WHERE bl."RepositoryBranchId" = b."Id" AND b."RepositoryId" = r."Id"
  AND NOT b."IsDeleted" AND NOT r."IsDeleted" AND NOT bl."IsDeleted"
  AND bl."LanguageCode" NOT IN ('en', 'vi');

-- Keep generated content recoverable. Reuse a language row if it already exists.
INSERT INTO "BranchLanguages"
    ("Id", "RepositoryBranchId", "LanguageCode", "IsDefault", "MindMapStatus",
     "CreatedAt", "IsDeleted")
SELECT gen_random_uuid()::text, b."Id", lang.code, lang.code = 'en',
       CASE WHEN b."GenerationStatus" IN (3, 4) THEN 3 ELSE 0 END, now(), false
FROM "RepositoryBranches" b
JOIN "Repositories" r ON r."Id" = b."RepositoryId"
CROSS JOIN (VALUES ('en'), ('vi')) AS lang(code)
WHERE NOT b."IsDeleted" AND NOT r."IsDeleted"
ON CONFLICT ("RepositoryBranchId", "LanguageCode") DO UPDATE
SET "IsDeleted" = false, "DeletedAt" = NULL,
    "IsDefault" = EXCLUDED."IsDefault", "UpdatedAt" = now();

-- A cancelled branch must not start a background mind map on API startup.
UPDATE "BranchLanguages" bl
SET "MindMapStatus" = 3, "UpdatedAt" = now()
FROM "RepositoryBranches" b
WHERE bl."RepositoryBranchId" = b."Id" AND NOT bl."IsDeleted"
  AND b."GenerationStatus" = 4 AND bl."MindMapStatus" IN (0, 1);

UPDATE "TranslationTasks" t
SET "IsDeleted" = true, "DeletedAt" = now(), "UpdatedAt" = now()
FROM "BranchLanguages" source
WHERE t."SourceBranchLanguageId" = source."Id" AND NOT t."IsDeleted"
  AND (t."TargetLanguageCode" NOT IN ('en', 'vi') OR source."IsDeleted");

DO $$
BEGIN
    IF EXISTS (
        SELECT b."Id"
        FROM "RepositoryBranches" b
        JOIN "Repositories" r ON r."Id" = b."RepositoryId"
        LEFT JOIN "BranchLanguages" bl ON bl."RepositoryBranchId" = b."Id"
            AND NOT bl."IsDeleted"
        WHERE NOT b."IsDeleted" AND NOT r."IsDeleted"
        GROUP BY b."Id"
        HAVING string_agg(bl."LanguageCode", ',' ORDER BY bl."LanguageCode")
            IS DISTINCT FROM 'en,vi'
            OR count(*) FILTER (WHERE bl."IsDefault" AND bl."LanguageCode" = 'en') <> 1
    ) THEN
        RAISE EXCEPTION 'Each active branch must have English and Vietnamese only';
    END IF;
END $$;

COMMIT;

SELECT r."RepoName", b."BranchName", b."GenerationStatus",
       string_agg(bl."LanguageCode", ',' ORDER BY bl."LanguageCode") AS "Languages"
FROM "RepositoryBranches" b
JOIN "Repositories" r ON r."Id" = b."RepositoryId"
JOIN "BranchLanguages" bl ON bl."RepositoryBranchId" = b."Id" AND NOT bl."IsDeleted"
WHERE NOT b."IsDeleted" AND NOT r."IsDeleted"
GROUP BY r."RepoName", b."Id";
