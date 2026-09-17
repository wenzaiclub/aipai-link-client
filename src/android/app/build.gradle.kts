import java.util.Properties

plugins {
    id("com.android.application")
}

// 证书信息放 keystore.properties（不入库，模板见 keystore.properties.example）。
// 没有这个文件时跳过签名，照常编译出未签名 APK——CI 上做编译检查就靠这个。
val keystoreProps = Properties().apply {
    val f = rootProject.file("keystore.properties")
    if (f.exists()) {
        f.inputStream().use { load(it) }
    }
}
val hasKeystore = keystoreProps.getProperty("storeFile") != null

android {
    namespace = "cn.appiie.net"
    compileSdk = 35
    buildToolsVersion = "36.0.0"

    defaultConfig {
        applicationId = "cn.appiie.net"
        minSdk = 24
        targetSdk = 34
        versionCode = 6
        versionName = "1.0.2"
    }

    signingConfigs {
        if (hasKeystore) {
            create("aipai") {
                storeFile = rootProject.file(keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            // 先不混淆：混淆对小体积帮助有限，却更容易出难查的问题
            isMinifyEnabled = false
            isShrinkResources = false
            if (hasKeystore) {
                signingConfig = signingConfigs.getByName("aipai")
            }
        }
        debug {
            if (hasKeystore) {
                signingConfig = signingConfigs.getByName("aipai")
            }
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // 两个 so 相互依赖，释放到 lib 目录更稳
    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    lint {
        abortOnError = false
    }
}
