# gMSA: service accounts without manually distributed passwords

A nightly export needs to run on `srv-job01` and place its results on a file share. The process requires a domain identity. A personal user account would tie ownership to the wrong person, while a conventional service account would also require password maintenance. A group Managed Service Account, or gMSA, is a possible choice for this bounded task.

The example assumes an application that supports running under a gMSA. The account type alone cannot make an incompatible sign-in implementation work.

## Two permissions that remain separate

Active Directory manages the gMSA password. Authorized hosts can retrieve it, removing the need for administrators to update a fixed password value in every service configuration. The computers allowed to retrieve it are explicitly defined. Microsoft describes using a security group containing the relevant computer accounts for this purpose. [Microsoft: Manage gMSAs](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/manage-group-managed-service-accounts).

The host's retrieval permission and the service account's resource permissions serve different purposes. In the export example, `srv-job01` may use the account. On the file server, the gMSA receives the required share and NTFS permissions on the export directory. The first permission does not imply the second.

Even “write access” needs a precise meaning. Does the export replace existing files or only create new ones, and who removes expired results? Separating those tasks allows narrower permissions. A second, unrelated service should not reuse the account merely because access to the share already works.

## Check prerequisites before changing the service

AD preparation includes an existing, effective KDS root key. When the first key is created, Microsoft allows a waiting period of up to ten hours for replication. The documented backdating procedure for a test environment with a single domain controller is not a general production rollout step. [Microsoft: Create a KDS root key](https://learn.microsoft.com/en-us/windows-server/identity/ad-ds/manage/group-managed-service-accounts/group-managed-service-accounts/create-the-key-distribution-services-kds-root-key).

After central provisioning, check whether the account is ready on its intended execution host. The following read-only test requires the ActiveDirectory module and an account that has already been prepared:

```powershell
Test-ADServiceAccount -Identity 'gmsaExport'
```

`True` confirms local usability of the managed account within the scope of this test. It does not test the export process or its access to the destination directory. [Microsoft: Test-ADServiceAccount](https://learn.microsoft.com/en-us/powershell/module/activedirectory/test-adserviceaccount).

The application-specific transition follows afterwards. The service configuration must support this account type and have the necessary logon rights. If a product insists on a manually entered password, its documentation should establish the supported configuration. Retrieving the managed password and copying it into an ordinary password field would bypass the intended management mechanism.

## Acceptance testing belongs inside the service

A successful manual write under an administrator account says nothing useful about the export's access. The test job must be started by the eventual service and create an identifiable test file in the intended directory. Its content and ownership can then be checked against expectations, along with continued protection of other directories.

Operational handover includes the account owner and the list of authorized hosts. When replacing `srv-job01`, the new host needs preparation and the old host must be removed from the authorization. Automatic password management does not perform this administrative housekeeping.

If NodePilot triggers the export, its execution identity and that of the export service still need separate consideration. A target service's support for gMSA does not establish general support for that account type in arbitrary credential fields.
