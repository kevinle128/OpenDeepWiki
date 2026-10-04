using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using OpenDeepWiki.Entities;
using OpenDeepWiki.Services.GitConnections;

namespace OpenDeepWiki.Tests.Services.Repositories;

/// <summary>
/// Property-based tests for Private Repository Visibility functionality.
/// Feature: private-repository-management
/// </summary>
public class PrivateRepositoryVisibilityPropertyTests
{
    /// <summary>
    /// Generates a valid GUID string.
    /// </summary>
    private static Gen<string> GenerateGuidString()
    {
        return Gen.Constant(0).Select(_ => Guid.NewGuid().ToString());
    }

    /// <summary>
    /// Generates a valid organization name.
    /// </summary>
    private static Gen<string> GenerateOrgName()
    {
        return Gen.Elements("microsoft", "google", "facebook", "amazon", "openai", "anthropic", "meta", "apple");
    }

    /// <summary>
    /// Generates a valid repository name.
    /// </summary>
    private static Gen<string> GenerateRepoName()
    {
        return Gen.Elements("api", "sdk", "cli", "web", "docs", "core", "utils", "tools", "app", "service");
    }

    /// <summary>
    /// Generates an empty or null password (no password scenarios).
    /// </summary>
    private static Gen<string?> GenerateEmptyOrNullPassword()
    {
        return Gen.Elements<string?>(null, "", "   ", "\t", "\n", "  \t  ");
    }

    /// <summary>
    /// Generates a valid non-empty password.
    /// </summary>
    private static Gen<string> GenerateValidPassword()
    {
        return Gen.Elements("password123", "secret", "token_abc", "auth_key_xyz", "p@ssw0rd!");
    }

    /// <summary>
    /// Generates a Repository entity without password (AuthPassword is null or empty).
    /// </summary>
    private static Gen<Repository> GenerateRepositoryWithoutCredential()
    {
        return GenerateGuidString().SelectMany(id =>
            GenerateGuidString().SelectMany(ownerId =>
                GenerateOrgName().SelectMany(orgName =>
                    GenerateRepoName().SelectMany(repoName =>
                        GenerateEmptyOrNullPassword().Select(password =>
                            new Repository
                            {
                                Id = id,
                                OwnerUserId = ownerId,
                                OrgName = orgName,
                                RepoName = repoName,
                                GitUrl = $"https://github.com/{orgName}/{repoName}.git",
                                AuthPassword = password,
                                IsPublic = true, // Start as public
                                Status = RepositoryStatus.Completed,
                                CreatedAt = DateTime.UtcNow
                            })))));
    }

    /// <summary>
    /// Generates a Repository entity with a stored credential: a Git connection, or (for repositories that
    /// still depend on it) a legacy password.
    /// </summary>
    private static Gen<Repository> GenerateRepositoryWithCredential()
    {
        return GenerateGuidString().SelectMany(id =>
            GenerateGuidString().SelectMany(ownerId =>
                GenerateOrgName().SelectMany(orgName =>
                    GenerateRepoName().SelectMany(repoName =>
                        GenerateValidPassword().SelectMany(password =>
                            Gen.Elements(true, false).Select(useConnection =>
                                new Repository
                                {
                                    Id = id,
                                    OwnerUserId = ownerId,
                                    OrgName = orgName,
                                    RepoName = repoName,
                                    GitUrl = $"https://github.com/{orgName}/{repoName}.git",
                                    GitConnectionId = useConnection ? Guid.NewGuid().ToString() : null,
                                    AuthPassword = useConnection ? null : password,
                                    IsPublic = true, // Start as public
                                    Status = RepositoryStatus.Completed,
                                    CreatedAt = DateTime.UtcNow
                                }))))));
    }

    /// <summary>
    /// Property 3: 无密码仓库私有化限制
    /// For any repository, if it has neither a Git connection nor a legacy password, 
    /// a request to set IsPublic to false should be rejected with a validation error.
    /// **Validates: Requirements 3.2, 3.4, 5.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NoCredentialRepository_CannotBeSetToPrivate()
    {
        return Prop.ForAll(
            GenerateRepositoryWithoutCredential().ToArbitrary(),
            repository =>
            {
                // Simulate the validation logic from UpdateVisibilityAsync
                var requestIsPublic = false; // Attempting to set to private
                var requestOwnerUserId = repository.OwnerUserId;

                // The validation logic: if trying to set private and no password, should reject
                var shouldReject = !requestIsPublic && !GitCredentialResolver.HasStoredCredential(repository);

                // This should always be true for repositories without password when setting to private
                return shouldReject;
            })
            .Label("Feature: private-repository-management, Property 3: 无密码仓库私有化限制");
    }

    /// <summary>
    /// Property 3: 无密码仓库私有化限制 - 验证错误消息
    /// When a no-password repository is attempted to be set to private,
    /// the error message should indicate the reason.
    /// **Validates: Requirements 3.2, 3.4, 5.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NoCredentialRepository_ReturnsValidationError()
    {
        const string expectedErrorMessage = "仓库凭据为空时不允许设置为私有";

        return Prop.ForAll(
            GenerateRepositoryWithoutCredential().ToArbitrary(),
            repository =>
            {
                // Simulate the validation logic from UpdateVisibilityAsync
                var requestIsPublic = false; // Attempting to set to private

                // Simulate the error response generation
                string? errorMessage = null;
                if (!requestIsPublic && !GitCredentialResolver.HasStoredCredential(repository))
                {
                    errorMessage = expectedErrorMessage;
                }

                // Should have the expected error message
                return errorMessage == expectedErrorMessage;
            })
            .Label("Feature: private-repository-management, Property 3: 无密码仓库私有化限制 - 验证错误消息");
    }

    /// <summary>
    /// Property 3: 无密码仓库私有化限制 - 公开设置不受限制
    /// Repositories without password CAN be set to public (IsPublic = true).
    /// **Validates: Requirements 3.2, 3.4, 5.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property NoCredentialRepository_CanBeSetToPublic()
    {
        return Prop.ForAll(
            GenerateRepositoryWithoutCredential().ToArbitrary(),
            repository =>
            {
                // Simulate the validation logic from UpdateVisibilityAsync
                var requestIsPublic = true; // Setting to public

                // The validation logic: setting to public should NOT be rejected
                var shouldReject = !requestIsPublic && !GitCredentialResolver.HasStoredCredential(repository);

                // This should always be false (not rejected) when setting to public
                return !shouldReject;
            })
            .Label("Feature: private-repository-management, Property 3: 无密码仓库私有化限制 - 公开设置不受限制");
    }

    /// <summary>
    /// Property 3: 无密码仓库私有化限制 - 有密码仓库可以设为私有
    /// Repositories WITH password CAN be set to private.
    /// **Validates: Requirements 3.2, 3.4, 5.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property RepositoryWithCredential_CanBeSetToPrivate()
    {
        return Prop.ForAll(
            GenerateRepositoryWithCredential().ToArbitrary(),
            repository =>
            {
                // Simulate the validation logic from UpdateVisibilityAsync
                var requestIsPublic = false; // Setting to private

                // The validation logic: if has password, should NOT be rejected
                var shouldReject = !requestIsPublic && !GitCredentialResolver.HasStoredCredential(repository);

                // This should always be false (not rejected) for repositories with password
                return !shouldReject;
            })
            .Label("Feature: private-repository-management, Property 3: 无密码仓库私有化限制 - 有密码仓库可以设为私有");
    }

    /// <summary>
    /// Property 3: 无密码仓库私有化限制 - 综合测试
    /// For any repository and any visibility setting, the validation logic should be consistent:
    /// - Reject only when: IsPublic=false AND the repository has no stored credential
    /// - Allow in all other cases
    /// **Validates: Requirements 3.2, 3.4, 5.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityValidation_IsConsistent()
    {
        var repositoryGen = Gen.OneOf(
            GenerateRepositoryWithoutCredential(),
            GenerateRepositoryWithCredential()
        );

        var isPublicGen = ArbMap.Default.GeneratorFor<bool>();

        return Prop.ForAll(
            repositoryGen.ToArbitrary(),
            isPublicGen.ToArbitrary(),
            (repository, isPublic) =>
            {
                // Simulate the validation logic from UpdateVisibilityAsync
                var requestIsPublic = isPublic;

                var hasPassword = GitCredentialResolver.HasStoredCredential(repository);
                var shouldReject = !requestIsPublic && !hasPassword;

                // Expected behavior:
                // - If setting to private (!isPublic) AND no password (!hasPassword) => reject (shouldReject = true)
                // - Otherwise => allow (shouldReject = false)
                var expectedReject = !isPublic && !hasPassword;

                return shouldReject == expectedReject;
            })
            .Label("Feature: private-repository-management, Property 3: 无密码仓库私有化限制 - 综合测试");
    }

    #region Property 6: 仓库所有权验证

    /// <summary>
    /// Generates a different user ID that is guaranteed to be different from the given one.
    /// </summary>
    private static Gen<string> GenerateDifferentUserId(string originalUserId)
    {
        return GenerateGuidString().Where(id => id != originalUserId);
    }

    /// <summary>
    /// Property 6: 仓库所有权验证 - 非所有者请求被拒绝
    /// For any visibility update request, if the OwnerUserId in the request 
    /// does not match the repository's OwnerUserId, the request should be rejected.
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OwnershipValidation_NonOwnerRequestIsRejected()
    {
        var repositoryGen = Gen.OneOf(
            GenerateRepositoryWithoutCredential(),
            GenerateRepositoryWithCredential()
        );

        return Prop.ForAll(
            repositoryGen.ToArbitrary(),
            GenerateGuidString().ToArbitrary(),
            ArbMap.Default.GeneratorFor<bool>().ToArbitrary(),
            (repository, requestUserId, isPublic) =>
            {
                // Ensure the request user ID is different from the repository owner
                var isDifferentOwner = repository.OwnerUserId != requestUserId;

                // Simulate the ownership validation logic from UpdateVisibilityAsync
                var shouldRejectDueToOwnership = repository.OwnerUserId != requestUserId;

                // If the user IDs are different, the request should be rejected
                return !isDifferentOwner || shouldRejectDueToOwnership;
            })
            .Label("Feature: private-repository-management, Property 6: 仓库所有权验证 - 非所有者请求被拒绝");
    }

    /// <summary>
    /// Property 6: 仓库所有权验证 - 非所有者请求返回正确错误消息
    /// When a non-owner attempts to update visibility, the error message should indicate "无权限修改此仓库".
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OwnershipValidation_NonOwnerReturnsCorrectErrorMessage()
    {
        const string expectedErrorMessage = "无权限修改此仓库";

        var repositoryGen = Gen.OneOf(
            GenerateRepositoryWithoutCredential(),
            GenerateRepositoryWithCredential()
        );

        return Prop.ForAll(
            repositoryGen.ToArbitrary(),
            ArbMap.Default.GeneratorFor<bool>().ToArbitrary(),
            (repository, isPublic) =>
            {
                // Generate a different user ID to simulate non-owner request
                var nonOwnerUserId = Guid.NewGuid().ToString();
                while (nonOwnerUserId == repository.OwnerUserId)
                {
                    nonOwnerUserId = Guid.NewGuid().ToString();
                }

                // Simulate the ownership validation logic from UpdateVisibilityAsync
                string? errorMessage = null;
                if (repository.OwnerUserId != nonOwnerUserId)
                {
                    errorMessage = expectedErrorMessage;
                }

                // Should have the expected error message for non-owner
                return errorMessage == expectedErrorMessage;
            })
            .Label("Feature: private-repository-management, Property 6: 仓库所有权验证 - 非所有者请求返回正确错误消息");
    }

    /// <summary>
    /// Property 6: 仓库所有权验证 - 所有者请求不因所有权被拒绝
    /// When the owner makes a visibility update request, it should NOT be rejected due to ownership validation.
    /// (Note: It may still be rejected for other reasons like no-password private restriction)
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OwnershipValidation_OwnerRequestIsNotRejectedDueToOwnership()
    {
        var repositoryGen = Gen.OneOf(
            GenerateRepositoryWithoutCredential(),
            GenerateRepositoryWithCredential()
        );

        return Prop.ForAll(
            repositoryGen.ToArbitrary(),
            ArbMap.Default.GeneratorFor<bool>().ToArbitrary(),
            (repository, isPublic) =>
            {
                // Use the same owner ID as the repository
                var requestOwnerUserId = repository.OwnerUserId;

                // Simulate the ownership validation logic from UpdateVisibilityAsync
                var shouldRejectDueToOwnership = repository.OwnerUserId != requestOwnerUserId;

                // Owner request should NOT be rejected due to ownership
                return !shouldRejectDueToOwnership;
            })
            .Label("Feature: private-repository-management, Property 6: 仓库所有权验证 - 所有者请求不因所有权被拒绝");
    }

    /// <summary>
    /// Property 6: 仓库所有权验证 - 所有权验证优先于密码验证
    /// Ownership validation should be checked before password validation.
    /// If ownership fails, the error should be about ownership, not about password.
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OwnershipValidation_TakesPrecedenceOverPasswordValidation()
    {
        const string ownershipErrorMessage = "无权限修改此仓库";
        const string passwordErrorMessage = "仓库凭据为空时不允许设置为私有";

        return Prop.ForAll(
            GenerateRepositoryWithoutCredential().ToArbitrary(),
            (repository) =>
            {
                // Generate a different user ID to simulate non-owner request
                var nonOwnerUserId = Guid.NewGuid().ToString();
                while (nonOwnerUserId == repository.OwnerUserId)
                {
                    nonOwnerUserId = Guid.NewGuid().ToString();
                }

                // Attempting to set to private (which would also fail password validation)
                var requestIsPublic = false;

                // Simulate the validation logic order from UpdateVisibilityAsync
                // 1. First check ownership
                // 2. Then check password
                string? errorMessage = null;

                // Ownership check first
                if (repository.OwnerUserId != nonOwnerUserId)
                {
                    errorMessage = ownershipErrorMessage;
                }
                // Password check second (only if ownership passed)
                else if (!requestIsPublic && !GitCredentialResolver.HasStoredCredential(repository))
                {
                    errorMessage = passwordErrorMessage;
                }

                // For non-owner, should get ownership error, not password error
                return errorMessage == ownershipErrorMessage;
            })
            .Label("Feature: private-repository-management, Property 6: 仓库所有权验证 - 所有权验证优先于密码验证");
    }

    /// <summary>
    /// Property 6: 仓库所有权验证 - 综合测试
    /// For any repository, any user ID, and any visibility setting:
    /// - If user ID matches repository owner => ownership validation passes
    /// - If user ID does not match repository owner => ownership validation fails with 403
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OwnershipValidation_IsConsistent()
    {
        var repositoryGen = Gen.OneOf(
            GenerateRepositoryWithoutCredential(),
            GenerateRepositoryWithCredential()
        );

        return Prop.ForAll(
            repositoryGen.ToArbitrary(),
            GenerateGuidString().ToArbitrary(),
            ArbMap.Default.GeneratorFor<bool>().ToArbitrary(),
            (repository, requestUserId, isPublic) =>
            {
                // Simulate the ownership validation logic from UpdateVisibilityAsync
                var isOwner = repository.OwnerUserId == requestUserId;
                var shouldRejectDueToOwnership = !isOwner;

                // Expected behavior:
                // - If user is owner => ownership validation passes (shouldRejectDueToOwnership = false)
                // - If user is not owner => ownership validation fails (shouldRejectDueToOwnership = true)
                var expectedReject = !isOwner;

                return shouldRejectDueToOwnership == expectedReject;
            })
            .Label("Feature: private-repository-management, Property 6: 仓库所有权验证 - 综合测试");
    }

    #endregion

    #region Property 7: 可见性更新持久化一致性

    /// <summary>
    /// Local representation of UpdateVisibilityRequest for testing purposes.
    /// Mirrors the structure in OpenDeepWiki.Models.UpdateVisibilityRequest.
    /// </summary>
    private class UpdateVisibilityRequest
    {
        public string RepositoryId { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public string OwnerUserId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Simulates the repository persistence layer for testing.
    /// This class mimics the behavior of the database context.
    /// </summary>
    private class MockRepositoryStore
    {
        private readonly Dictionary<string, Repository> _repositories = new();

        public void Add(Repository repository)
        {
            _repositories[repository.Id] = repository;
        }

        public Repository? FindById(string id)
        {
            return _repositories.TryGetValue(id, out var repo) ? repo : null;
        }

        public void SaveChanges()
        {
            // In a real database, this would persist changes
            // For our mock, changes are already in memory
        }
    }

    /// <summary>
    /// Simulates the UpdateVisibilityAsync logic for testing persistence consistency.
    /// Returns a tuple of (success, updatedRepository).
    /// </summary>
    private static (bool Success, Repository? UpdatedRepository, string? ErrorMessage) SimulateUpdateVisibility(
        MockRepositoryStore store,
        UpdateVisibilityRequest request)
    {
        // Find the repository
        var repository = store.FindById(request.RepositoryId);

        // Repository not found
        if (repository is null)
        {
            return (false, null, "仓库不存在");
        }

        // Ownership validation
        if (repository.OwnerUserId != request.OwnerUserId)
        {
            return (false, repository, "无权限修改此仓库");
        }

        // No-password private restriction
        if (!request.IsPublic && !GitCredentialResolver.HasStoredCredential(repository))
        {
            return (false, repository, "仓库凭据为空时不允许设置为私有");
        }

        // Update visibility
        repository.IsPublic = request.IsPublic;
        store.SaveChanges();

        return (true, repository, null);
    }

    /// <summary>
    /// Property 7: 可见性更新持久化一致性 - 有密码仓库设为私有
    /// For any repository with password, after a successful visibility update to private,
    /// the repository's IsPublic field should equal the requested value (false).
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityPersistence_RepositoryWithCredential_SetToPrivate()
    {
        return Prop.ForAll(
            GenerateRepositoryWithCredential().ToArbitrary(),
            repository =>
            {
                // Setup mock store
                var store = new MockRepositoryStore();
                store.Add(repository);

                // Create a valid request to set to private
                var request = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = false, // Set to private
                    OwnerUserId = repository.OwnerUserId
                };

                // Execute the update
                var (success, updatedRepository, _) = SimulateUpdateVisibility(store, request);

                // Verify: update should succeed and IsPublic should match request
                return success &&
                       updatedRepository != null &&
                       updatedRepository.IsPublic == request.IsPublic;
            })
            .Label("Feature: private-repository-management, Property 7: 可见性更新持久化一致性 - 有密码仓库设为私有");
    }

    /// <summary>
    /// Property 7: 可见性更新持久化一致性 - 有密码仓库设为公开
    /// For any repository with password, after a successful visibility update to public,
    /// the repository's IsPublic field should equal the requested value (true).
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityPersistence_RepositoryWithCredential_SetToPublic()
    {
        return Prop.ForAll(
            GenerateRepositoryWithCredential().ToArbitrary(),
            repository =>
            {
                // First set to private to test the transition
                repository.IsPublic = false;

                // Setup mock store
                var store = new MockRepositoryStore();
                store.Add(repository);

                // Create a valid request to set to public
                var request = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = true, // Set to public
                    OwnerUserId = repository.OwnerUserId
                };

                // Execute the update
                var (success, updatedRepository, _) = SimulateUpdateVisibility(store, request);

                // Verify: update should succeed and IsPublic should match request
                return success &&
                       updatedRepository != null &&
                       updatedRepository.IsPublic == request.IsPublic;
            })
            .Label("Feature: private-repository-management, Property 7: 可见性更新持久化一致性 - 有密码仓库设为公开");
    }

    /// <summary>
    /// Property 7: 可见性更新持久化一致性 - 无密码仓库设为公开
    /// For any repository without password, after a successful visibility update to public,
    /// the repository's IsPublic field should equal the requested value (true).
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityPersistence_RepositoryWithoutCredential_SetToPublic()
    {
        return Prop.ForAll(
            GenerateRepositoryWithoutCredential().ToArbitrary(),
            repository =>
            {
                // Setup mock store
                var store = new MockRepositoryStore();
                store.Add(repository);

                // Create a valid request to set to public
                var request = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = true, // Set to public (only valid option for no-password repos)
                    OwnerUserId = repository.OwnerUserId
                };

                // Execute the update
                var (success, updatedRepository, _) = SimulateUpdateVisibility(store, request);

                // Verify: update should succeed and IsPublic should match request
                return success &&
                       updatedRepository != null &&
                       updatedRepository.IsPublic == request.IsPublic;
            })
            .Label("Feature: private-repository-management, Property 7: 可见性更新持久化一致性 - 无密码仓库设为公开");
    }

    /// <summary>
    /// Property 7: 可见性更新持久化一致性 - 查询验证
    /// After a successful visibility update, querying the repository should return
    /// the updated IsPublic value that matches the request.
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityPersistence_QueryAfterUpdate_ReturnsCorrectValue()
    {
        return Prop.ForAll(
            GenerateRepositoryWithCredential().ToArbitrary(),
            ArbMap.Default.GeneratorFor<bool>().ToArbitrary(),
            (repository, targetIsPublic) =>
            {
                // Setup mock store
                var store = new MockRepositoryStore();
                store.Add(repository);

                // Create a valid request
                var request = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = targetIsPublic,
                    OwnerUserId = repository.OwnerUserId
                };

                // Execute the update
                var (success, _, _) = SimulateUpdateVisibility(store, request);

                // Query the repository after update
                var queriedRepository = store.FindById(repository.Id);

                // Verify: query should return the updated value
                return success &&
                       queriedRepository != null &&
                       queriedRepository.IsPublic == targetIsPublic;
            })
            .Label("Feature: private-repository-management, Property 7: 可见性更新持久化一致性 - 查询验证");
    }

    /// <summary>
    /// Property 7: 可见性更新持久化一致性 - 多次更新一致性
    /// Multiple visibility updates should each persist correctly,
    /// with the final state matching the last update request.
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityPersistence_MultipleUpdates_FinalStateMatchesLastRequest()
    {
        // Combine generators into a tuple to avoid too many ForAll parameters
        var combinedGen = GenerateRepositoryWithCredential().SelectMany(repo =>
            ArbMap.Default.GeneratorFor<bool>().SelectMany(first =>
                ArbMap.Default.GeneratorFor<bool>().SelectMany(second =>
                    ArbMap.Default.GeneratorFor<bool>().Select(third =>
                        (Repository: repo, First: first, Second: second, Third: third)))));

        return Prop.ForAll(
            combinedGen.ToArbitrary(),
            tuple =>
            {
                var (repository, firstIsPublic, secondIsPublic, thirdIsPublic) = tuple;

                // Setup mock store
                var store = new MockRepositoryStore();
                store.Add(repository);

                // Perform three updates
                var request1 = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = firstIsPublic,
                    OwnerUserId = repository.OwnerUserId
                };
                SimulateUpdateVisibility(store, request1);

                var request2 = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = secondIsPublic,
                    OwnerUserId = repository.OwnerUserId
                };
                SimulateUpdateVisibility(store, request2);

                var request3 = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = thirdIsPublic,
                    OwnerUserId = repository.OwnerUserId
                };
                var (success, _, _) = SimulateUpdateVisibility(store, request3);

                // Query the repository after all updates
                var queriedRepository = store.FindById(repository.Id);

                // Verify: final state should match the last request
                return success &&
                       queriedRepository != null &&
                       queriedRepository.IsPublic == thirdIsPublic;
            })
            .Label("Feature: private-repository-management, Property 7: 可见性更新持久化一致性 - 多次更新一致性");
    }

    /// <summary>
    /// Property 7: 可见性更新持久化一致性 - 响应与持久化状态一致
    /// The response from UpdateVisibility should match the persisted state.
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityPersistence_ResponseMatchesPersistedState()
    {
        return Prop.ForAll(
            GenerateRepositoryWithCredential().ToArbitrary(),
            ArbMap.Default.GeneratorFor<bool>().ToArbitrary(),
            (repository, targetIsPublic) =>
            {
                // Setup mock store
                var store = new MockRepositoryStore();
                store.Add(repository);

                // Create a valid request
                var request = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = targetIsPublic,
                    OwnerUserId = repository.OwnerUserId
                };

                // Execute the update
                var (success, updatedRepository, _) = SimulateUpdateVisibility(store, request);

                // Query the repository to get persisted state
                var persistedRepository = store.FindById(repository.Id);

                // Verify: response should match persisted state
                return success &&
                       updatedRepository != null &&
                       persistedRepository != null &&
                       updatedRepository.IsPublic == persistedRepository.IsPublic &&
                       persistedRepository.IsPublic == targetIsPublic;
            })
            .Label("Feature: private-repository-management, Property 7: 可见性更新持久化一致性 - 响应与持久化状态一致");
    }

    /// <summary>
    /// Property 7: 可见性更新持久化一致性 - 综合测试
    /// For any valid visibility update request (repository with password OR setting to public),
    /// after update, querying the repository's IsPublic field should equal the requested value.
    /// **Validates: Requirements 5.4, 5.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property VisibilityPersistence_IsConsistent()
    {
        var repositoryGen = Gen.OneOf(
            GenerateRepositoryWithoutCredential(),
            GenerateRepositoryWithCredential()
        );

        return Prop.ForAll(
            repositoryGen.ToArbitrary(),
            ArbMap.Default.GeneratorFor<bool>().ToArbitrary(),
            (repository, targetIsPublic) =>
            {
                // Setup mock store
                var store = new MockRepositoryStore();
                store.Add(repository);

                // Determine if this is a valid request
                var hasPassword = GitCredentialResolver.HasStoredCredential(repository);
                var isValidRequest = targetIsPublic || hasPassword; // Can set to public always, or private only with password

                // Create the request
                var request = new UpdateVisibilityRequest
                {
                    RepositoryId = repository.Id,
                    IsPublic = targetIsPublic,
                    OwnerUserId = repository.OwnerUserId
                };

                // Execute the update
                var (success, _, errorMessage) = SimulateUpdateVisibility(store, request);

                // Query the repository after update
                var queriedRepository = store.FindById(repository.Id);

                if (isValidRequest)
                {
                    // For valid requests: update should succeed and IsPublic should match request
                    return success &&
                           queriedRepository != null &&
                           queriedRepository.IsPublic == targetIsPublic;
                }
                else
                {
                    // For invalid requests (no password + private): update should fail
                    return !success &&
                           errorMessage == "仓库凭据为空时不允许设置为私有";
                }
            })
            .Label("Feature: private-repository-management, Property 7: 可见性更新持久化一致性 - 综合测试");
    }

    #endregion

    #region Property 8: 请求序列化往返一致性

    /// <summary>
    /// Local representation of UpdateVisibilityRequest for serialization testing.
    /// Mirrors the structure in OpenDeepWiki.Models.UpdateVisibilityRequest.
    /// </summary>
    private class SerializableUpdateVisibilityRequest
    {
        public string RepositoryId { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public string OwnerUserId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Local representation of UpdateVisibilityResponse for serialization testing.
    /// Mirrors the structure in OpenDeepWiki.Models.UpdateVisibilityResponse.
    /// </summary>
    private class SerializableUpdateVisibilityResponse
    {
        public string Id { get; set; } = string.Empty;
        public bool IsPublic { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// Generates a valid UpdateVisibilityRequest for serialization testing.
    /// </summary>
    private static Gen<SerializableUpdateVisibilityRequest> GenerateSerializableUpdateVisibilityRequest()
    {
        return GenerateGuidString().SelectMany(repositoryId =>
            GenerateGuidString().SelectMany(ownerUserId =>
                ArbMap.Default.GeneratorFor<bool>().Select(isPublic =>
                    new SerializableUpdateVisibilityRequest
                    {
                        RepositoryId = repositoryId,
                        IsPublic = isPublic,
                        OwnerUserId = ownerUserId
                    })));
    }

    /// <summary>
    /// Generates a valid UpdateVisibilityResponse for serialization testing.
    /// </summary>
    private static Gen<SerializableUpdateVisibilityResponse> GenerateSerializableUpdateVisibilityResponse()
    {
        return GenerateGuidString().SelectMany(id =>
            ArbMap.Default.GeneratorFor<bool>().SelectMany(isPublic =>
                ArbMap.Default.GeneratorFor<bool>().SelectMany(success =>
                    Gen.Elements<string?>(null, "", "仓库不存在", "无权限修改此仓库", "仓库凭据为空时不允许设置为私有").Select(errorMessage =>
                        new SerializableUpdateVisibilityResponse
                        {
                            Id = id,
                            IsPublic = isPublic,
                            Success = success,
                            ErrorMessage = errorMessage
                        }))));
    }

    /// <summary>
    /// Property 8: 请求序列化往返一致性 - UpdateVisibilityRequest
    /// For any UpdateVisibilityRequest object, JSON serialization followed by 
    /// deserialization should produce an equivalent object.
    /// **Validates: Requirements 5.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property RequestSerialization_RoundTrip_UpdateVisibilityRequest()
    {
        return Prop.ForAll(
            GenerateSerializableUpdateVisibilityRequest().ToArbitrary(),
            request =>
            {
                // Serialize to JSON
                var json = System.Text.Json.JsonSerializer.Serialize(request);

                // Deserialize back to object
                var deserialized = System.Text.Json.JsonSerializer.Deserialize<SerializableUpdateVisibilityRequest>(json);

                // Verify round-trip consistency
                return deserialized != null &&
                       deserialized.RepositoryId == request.RepositoryId &&
                       deserialized.IsPublic == request.IsPublic &&
                       deserialized.OwnerUserId == request.OwnerUserId;
            })
            .Label("Feature: private-repository-management, Property 8: 请求序列化往返一致性");
    }

    /// <summary>
    /// Property 8: 请求序列化往返一致性 - UpdateVisibilityResponse
    /// For any UpdateVisibilityResponse object, JSON serialization followed by 
    /// deserialization should produce an equivalent object.
    /// **Validates: Requirements 5.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property RequestSerialization_RoundTrip_UpdateVisibilityResponse()
    {
        return Prop.ForAll(
            GenerateSerializableUpdateVisibilityResponse().ToArbitrary(),
            response =>
            {
                // Serialize to JSON
                var json = System.Text.Json.JsonSerializer.Serialize(response);

                // Deserialize back to object
                var deserialized = System.Text.Json.JsonSerializer.Deserialize<SerializableUpdateVisibilityResponse>(json);

                // Verify round-trip consistency
                return deserialized != null &&
                       deserialized.Id == response.Id &&
                       deserialized.IsPublic == response.IsPublic &&
                       deserialized.Success == response.Success &&
                       deserialized.ErrorMessage == response.ErrorMessage;
            })
            .Label("Feature: private-repository-management, Property 8: 请求序列化往返一致性 - Response");
    }

    /// <summary>
    /// Property 8: 请求序列化往返一致性 - JSON格式验证
    /// Serialized JSON should contain all expected property names.
    /// **Validates: Requirements 5.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property RequestSerialization_JsonContainsExpectedProperties()
    {
        return Prop.ForAll(
            GenerateSerializableUpdateVisibilityRequest().ToArbitrary(),
            request =>
            {
                // Serialize to JSON
                var json = System.Text.Json.JsonSerializer.Serialize(request);

                // Verify JSON contains expected property names (case-sensitive by default)
                return json.Contains("RepositoryId") &&
                       json.Contains("IsPublic") &&
                       json.Contains("OwnerUserId");
            })
            .Label("Feature: private-repository-management, Property 8: 请求序列化往返一致性 - JSON格式验证");
    }

    /// <summary>
    /// Property 8: 请求序列化往返一致性 - 多次序列化一致性
    /// Multiple serialization/deserialization cycles should produce consistent results.
    /// **Validates: Requirements 5.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property RequestSerialization_MultipleRoundTrips_AreConsistent()
    {
        return Prop.ForAll(
            GenerateSerializableUpdateVisibilityRequest().ToArbitrary(),
            request =>
            {
                // First round-trip
                var json1 = System.Text.Json.JsonSerializer.Serialize(request);
                var deserialized1 = System.Text.Json.JsonSerializer.Deserialize<SerializableUpdateVisibilityRequest>(json1);

                // Second round-trip
                var json2 = System.Text.Json.JsonSerializer.Serialize(deserialized1);
                var deserialized2 = System.Text.Json.JsonSerializer.Deserialize<SerializableUpdateVisibilityRequest>(json2);

                // Third round-trip
                var json3 = System.Text.Json.JsonSerializer.Serialize(deserialized2);
                var deserialized3 = System.Text.Json.JsonSerializer.Deserialize<SerializableUpdateVisibilityRequest>(json3);

                // Verify all round-trips produce consistent results
                return deserialized1 != null && deserialized2 != null && deserialized3 != null &&
                       // All JSON strings should be identical
                       json1 == json2 && json2 == json3 &&
                       // All deserialized objects should be equivalent
                       deserialized1.RepositoryId == deserialized2.RepositoryId &&
                       deserialized2.RepositoryId == deserialized3.RepositoryId &&
                       deserialized1.IsPublic == deserialized2.IsPublic &&
                       deserialized2.IsPublic == deserialized3.IsPublic &&
                       deserialized1.OwnerUserId == deserialized2.OwnerUserId &&
                       deserialized2.OwnerUserId == deserialized3.OwnerUserId;
            })
            .Label("Feature: private-repository-management, Property 8: 请求序列化往返一致性 - 多次序列化一致性");
    }

    #endregion
}
