use ashpd::{
    Error, PortalError, WindowIdentifier,
    desktop::{ResponseError, screenshot::Screenshot},
    url::Url,
    zbus,
};
use std::path::PathBuf;

pub async fn request_image(window: &adw::ApplicationWindow) -> Result<PathBuf, String> {
    // Older portals ignore the interactive hint. Do not fall back to that behavior.
    let version = screenshot_version()
        .await
        .map_err(|err| format!("Cannot query the desktop Screenshot portal: {err}"))?;
    if version < 2 {
        return Err(format!(
            "Screenshot portal v{version} does not support interactive requests (v2 required)."
        ));
    }
    let parent = WindowIdentifier::from_native(window)
        .await
        .ok_or("Cannot identify the capture window. Use a GNOME Wayland session.")?;
    let response = Screenshot::request()
        .identifier(parent)
        .interactive(true)
        .modal(true)
        .send()
        .await
        .and_then(|request| request.response())
        .map_err(request_error)?;
    local_image_path(response.uri()).ok_or_else(|| "The portal did not return a local file.".into())
}

async fn screenshot_version() -> zbus::Result<u32> {
    let bus = zbus::Connection::session().await?;
    let proxy = zbus::Proxy::new(
        &bus,
        "org.freedesktop.portal.Desktop",
        "/org/freedesktop/portal/desktop",
        "org.freedesktop.portal.Screenshot",
    )
    .await?;
    proxy.get_property("version").await
}

fn local_image_path(uri: &Url) -> Option<PathBuf> {
    if uri.scheme() != "file"
        || uri.host_str().is_some()
        || uri.fragment().is_some()
        || uri.query().is_some()
    {
        return None;
    }
    uri.to_file_path().ok().filter(|path| path.is_absolute())
}

fn request_error(err: Error) -> String {
    match err {
        Error::Response(ResponseError::Cancelled) | Error::Portal(PortalError::Cancelled(_)) => {
            "Screenshot canceled. No image was loaded.".into()
        }
        Error::Portal(PortalError::NotAllowed(_)) => {
            "Screenshot permission denied. No image was loaded.".into()
        }
        Error::Response(ResponseError::Other) => {
            "The portal declined or could not complete the request. No image was loaded.".into()
        }
        other => format!("Screenshot portal error: {other}"),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_local_document_uri_without_guessing_a_path() {
        let uri = Url::parse("file:///run/user/1000/doc/abc/capture%201.png").unwrap();
        assert_eq!(
            local_image_path(&uri),
            Some(PathBuf::from("/run/user/1000/doc/abc/capture 1.png"))
        );
    }

    #[test]
    fn rejects_remote_or_ambiguous_images() {
        for uri in [
            "https://example.org/image.png",
            "file://example.org/image.png",
            "data:image/png;base64,AAAA",
            "file:///image.png?other=1",
            "file:///image.png#fragment",
        ] {
            assert!(local_image_path(&Url::parse(uri).unwrap()).is_none());
        }
    }
}
