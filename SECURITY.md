# Security policy

Dijitalya.Imaging decodes files from untrusted sources, so a picture that makes a decoder crash with an
unexpected exception, hang, or allocate far more memory than its size justifies is a security issue.

## Reporting a vulnerability

Please do not open a public issue. Report it privately through the **Report a vulnerability** button on
the **Security** tab of the repository, and attach the file that triggers the problem if you can.

You will get an answer within a week. Fixes are released as a new patch version and recorded in
CHANGELOG.md.

## Supported versions

Only the latest released version receives security fixes.

## Operational limits

Use `ImageDecodeOptions` to set limits below the global ceilings for your workload. The allocation
budget covers accounted buffers, not process working set, native runtime overhead or the sum of
concurrent requests. Enforce service-level concurrency and request timeouts separately. Cancellation
is cooperative. Keep strict mode enabled for uploads; tolerant mode is for deliberate recovery.

A passing mutation suite does not prove that every malformed input is safe. Retain regression files
for confirmed failures and run the platform, interoperability and Native AOT checks before release.
