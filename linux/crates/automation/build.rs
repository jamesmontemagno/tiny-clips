use std::{env, fs, path::PathBuf};

fn main() {
    println!("cargo:rerun-if-env-changed=TINYCLIPS_CONTRACT_DIR");
    let dir = env::var_os("TINYCLIPS_CONTRACT_DIR")
        .map(PathBuf::from)
        .unwrap_or_else(|| {
            PathBuf::from(env!("CARGO_MANIFEST_DIR")).join("../../../shared/automation")
        });
    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    for (source, name) in [
        ("commands.json", "commands.json"),
        ("fixtures/policy.json", "policy.json"),
    ] {
        let source = dir.join(source);
        println!("cargo:rerun-if-changed={}", source.display());
        fs::copy(&source, out.join(name)).unwrap_or_else(|e| panic!("{}: {e}", source.display()));
    }
}
